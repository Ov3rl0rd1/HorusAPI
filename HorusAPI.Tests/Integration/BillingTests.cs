using System.Net;
using Dapper;
using HorusAPI.Tests.Infrastructure;
using Npgsql;

namespace HorusAPI.Tests.Integration;

/// <summary>
/// End-to-end billing: the entitlement access flip, checkout + provider-webhook activation
/// (recurring and one-time), idempotency, promos, capacity holds, cancellation, comp grants
/// and refunds. The acquirer is <see cref="FakePaymentProvider"/>, so these exercise our own
/// state machine, not Platega.
/// </summary>
public class BillingTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    // ── Access model ───────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Verified_user_without_a_subscription_cannot_select_a_server()
    {
        RequireDb();
        var client = Client();
        var (_, _, session) = await RegisterVerifiedUserAsync(client);

        var res = await client.PostJsonAsync("/servers/select", new { }, TestData.NewIp(), session);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("subscription_expired", await res.ReadStringPropAsync("code"));
    }

    // ── Checkout ─────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Checkout_rejects_an_unknown_plan()
    {
        RequireDb();
        var client = Client();
        var (_, _, session) = await RegisterVerifiedUserAsync(client);

        var res = await client.PostJsonAsync("/billing/checkout", new { plan_code = "does_not_exist" }, TestData.NewIp(), session);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [SkippableFact]
    public async Task Recurring_checkout_then_activation_grants_access()
    {
        RequireDb();
        await SeedServerAsync();
        string plan = await SeedPlanAsync(kind: "recurring", amount: 199);

        var client = Client();
        var (_, _, session) = await RegisterVerifiedUserAsync(client);

        var checkout = await client.PostJsonAsync("/billing/checkout", new { plan_code = plan }, TestData.NewIp(), session);
        Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);
        var body = await checkout.ReadJsonAsync();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("redirect").GetString()));
        Assert.Equal(199, body.GetProperty("amount").GetInt32());

        string providerRef = Fixture.Payments.LastRef!;
        await ActivateAsync(client, providerRef);

        var sub = await client.GetWithAsync("/billing/subscription", TestData.NewIp(), session);
        Assert.Equal("active", await sub.ReadStringPropAsync("status"));

        // Access is now granted — the user can bind to a server.
        var select = await client.PostJsonAsync("/servers/select", new { }, TestData.NewIp(), session);
        Assert.Equal(HttpStatusCode.OK, select.StatusCode);
    }

    [SkippableFact]
    public async Task Duplicate_activation_webhook_is_idempotent()
    {
        RequireDb();
        await SeedServerAsync();
        string plan = await SeedPlanAsync(kind: "recurring");

        var client = Client();
        var (username, _, session) = await RegisterVerifiedUserAsync(client);
        await client.PostJsonAsync("/billing/checkout", new { plan_code = plan }, TestData.NewIp(), session);
        string providerRef = Fixture.Payments.LastRef!;

        await ActivateAsync(client, providerRef);
        await ActivateAsync(client, providerRef);   // replay

        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        int active = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM subscriptions WHERE user_id = (SELECT id FROM users WHERE username = @username) AND status = 'active'",
            new { username });
        Assert.Equal(1, active);

        int events = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM webhook_events WHERE provider_event_id = @eid", new { eid = $"sub:{providerRef}:SUBSCRIPTION_ACTIVATED" });
        Assert.Equal(1, events);   // recorded once, replay short-circuits
    }

    [SkippableFact]
    public async Task One_time_checkout_then_confirm_grants_access()
    {
        RequireDb();
        await SeedServerAsync();
        string plan = await SeedPlanAsync(kind: "one_time", amount: 300, unit: "day", count: 30);

        var client = Client();
        var (_, _, session) = await RegisterVerifiedUserAsync(client);
        await client.PostJsonAsync("/billing/checkout", new { plan_code = plan }, TestData.NewIp(), session);
        string providerRef = Fixture.Payments.LastRef!;

        await PostWebhookAsync(client, new { id = providerRef, amount = 300, currency = "RUB", status = "CONFIRMED" });

        var sub = await client.GetWithAsync("/billing/subscription", TestData.NewIp(), session);
        Assert.Equal("active", await sub.ReadStringPropAsync("status"));
        Assert.Equal("one_time", await sub.ReadStringPropAsync("kind"));
    }

    // ── Promos ─────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Promo_is_rejected_for_recurring_plans()
    {
        RequireDb();
        await SeedServerAsync();
        string plan = await SeedPlanAsync(kind: "recurring", amount: 199);
        string promo = await SeedPromoAsync(percent: 20);

        var client = Client();
        var (_, _, session) = await RegisterVerifiedUserAsync(client);

        var res = await client.PostJsonAsync("/billing/checkout", new { plan_code = plan, promo_code = promo }, TestData.NewIp(), session);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("promo_not_applicable", await res.ReadStringPropAsync("code"));
    }

    [SkippableFact]
    public async Task Promo_discounts_a_one_time_purchase()
    {
        RequireDb();
        await SeedServerAsync();
        string plan = await SeedPlanAsync(kind: "one_time", amount: 200, unit: "month", count: 1);
        string promo = await SeedPromoAsync(percent: 25);

        var client = Client();
        var (_, _, session) = await RegisterVerifiedUserAsync(client);

        var res = await client.PostJsonAsync("/billing/checkout", new { plan_code = plan, promo_code = promo }, TestData.NewIp(), session);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.ReadJsonAsync();
        Assert.Equal(50, body.GetProperty("discount").GetInt32());
        Assert.Equal(150, body.GetProperty("amount").GetInt32());
    }

    // ── Capacity ─────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Checkout_is_refused_when_no_slots_are_free()
    {
        RequireDb();
        string plan = await SeedPlanAsync(kind: "recurring");

        var client = Client();
        var (_, _, session) = await RegisterVerifiedUserAsync(client);

        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        // Sequentially-run collection lets us take the fleet offline for this one case.
        await conn.ExecuteAsync("UPDATE vpn_servers SET is_active = FALSE");
        try
        {
            var res = await client.PostJsonAsync("/billing/checkout", new { plan_code = plan }, TestData.NewIp(), session);
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Equal("no_capacity", await res.ReadStringPropAsync("code"));
        }
        finally
        {
            await conn.ExecuteAsync("UPDATE vpn_servers SET is_active = TRUE");
        }
    }

    [SkippableFact]
    public async Task Provider_error_during_checkout_releases_the_held_slot()
    {
        RequireDb();
        await SeedServerAsync();
        string plan = await SeedPlanAsync(kind: "recurring");

        var client = Client();
        var (username, _, session) = await RegisterVerifiedUserAsync(client);

        Fixture.Payments.FailNextCreate = true;
        var res = await client.PostJsonAsync("/billing/checkout", new { plan_code = plan }, TestData.NewIp(), session);
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);

        // The hold must be gone — no leaked seat.
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        int holds = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM slot_holds WHERE user_id = (SELECT id FROM users WHERE username = @username)", new { username });
        Assert.Equal(0, holds);
    }

    // ── Cancel / comp / refund ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task User_can_cancel_auto_renew()
    {
        RequireDb();
        await SeedServerAsync();
        string plan = await SeedPlanAsync(kind: "recurring");

        var client = Client();
        var (_, _, session) = await RegisterVerifiedUserAsync(client);
        await client.PostJsonAsync("/billing/checkout", new { plan_code = plan }, TestData.NewIp(), session);
        string providerRef = Fixture.Payments.LastRef!;
        await ActivateAsync(client, providerRef);

        var cancel = await client.PostJsonAsync("/billing/cancel", new { }, TestData.NewIp(), session);
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        Assert.Contains(providerRef, Fixture.Payments.Canceled);

        var sub = await client.GetWithAsync("/billing/subscription", TestData.NewIp(), session);
        var body = await sub.ReadJsonAsync();
        Assert.True(body.GetProperty("cancel_at_period_end").GetBoolean());
    }

    [SkippableFact]
    public async Task Admin_comp_grants_then_revokes_access()
    {
        RequireDb();
        await SeedServerAsync();

        var client = Client();
        var (username, _, session) = await RegisterVerifiedUserAsync(client);
        string adminSession = await NewAdminSessionAsync(client);

        var grant = await client.PutJsonAsync($"/admin/users/{username}/subscription",
            new { expires_at = DateTime.UtcNow.AddMonths(1) }, TestData.NewIp(), adminSession);
        Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);

        var select = await client.PostJsonAsync("/servers/select", new { }, TestData.NewIp(), session);
        Assert.Equal(HttpStatusCode.OK, select.StatusCode);

        var revoke = await client.DeleteWithAsync($"/admin/users/{username}/subscription", TestData.NewIp(), adminSession);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var denied = await client.PostJsonAsync("/servers/select", new { }, TestData.NewIp(), session);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_refund_revokes_access()
    {
        RequireDb();
        await SeedServerAsync();
        string plan = await SeedPlanAsync(kind: "one_time", amount: 300);

        var client = Client();
        var (username, _, session) = await RegisterVerifiedUserAsync(client);
        await client.PostJsonAsync("/billing/checkout", new { plan_code = plan }, TestData.NewIp(), session);
        string providerRef = Fixture.Payments.LastRef!;
        await PostWebhookAsync(client, new { id = providerRef, amount = 300, currency = "RUB", status = "CONFIRMED" });

        // Access confirmed first.
        Assert.Equal(HttpStatusCode.OK, (await client.PostJsonAsync("/servers/select", new { }, TestData.NewIp(), session)).StatusCode);

        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        int paymentId = await conn.ExecuteScalarAsync<int>(
            "SELECT id FROM payments WHERE provider_ref = @ref", new { @ref = providerRef });

        string adminSession = await NewAdminSessionAsync(client);
        var refund = await client.PostJsonAsync($"/admin/payments/{paymentId}/refund", new { reason = "test" }, TestData.NewIp(), adminSession);
        Assert.Equal(HttpStatusCode.OK, refund.StatusCode);
        Assert.Equal("refunded", await refund.ReadStringPropAsync("status"));

        var denied = await client.PostJsonAsync("/servers/select", new { }, TestData.NewIp(), session);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    private Task ActivateAsync(HttpClient client, string providerRef) => PostWebhookAsync(client, new
    {
        Id = providerRef,
        Amount = 199,
        Currency = "RUB",
        Status = "SUBSCRIPTION_ACTIVATED",
        PaymentMethod = 6,
        SubscriptionId = providerRef,
        NextChargeAt = DateTime.UtcNow.AddMonths(1)
    });

    private static async Task PostWebhookAsync(HttpClient client, object body)
    {
        var res = await client.PostJsonAsync("/payments/platega/webhook", body, TestData.NewIp());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    private async Task<string> SeedPlanAsync(string kind, int amount = 199, bool isPublic = true, string unit = "month", int count = 1)
    {
        string code = "plan_" + Guid.NewGuid().ToString("N")[..10];
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.ExecuteAsync("""
            INSERT INTO plans (code, title, tier, kind, interval_unit, interval_count, amount, is_public)
            VALUES (@code, @code, 'standard', @kind, @unit, @count, @amount, @pub)
            """, new { code, kind, unit, count, amount, pub = isPublic });
        return code;
    }

    private async Task<string> SeedPromoAsync(int percent)
    {
        string code = "promo_" + Guid.NewGuid().ToString("N")[..8];
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.ExecuteAsync("""
            INSERT INTO promo_codes (code, kind, percent_off, is_active) VALUES (@code, 'percent', @pct, TRUE)
            """, new { code, pct = (short)percent });
        return code;
    }

    private async Task SeedServerAsync(int maxClients = 5)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.ExecuteAsync("""
            INSERT INTO vpn_servers (name, country, city, host, max_clients, auth_password, is_active)
            VALUES ('T', 'TT', 'TC', @host, @m, 'pw', TRUE)
            """, new { host = "node-" + Guid.NewGuid().ToString("N")[..8] + ".example", m = maxClients });
    }

    private async Task<string> NewAdminSessionAsync(HttpClient client)
    {
        var (username, _, _) = await RegisterVerifiedUserAsync(client);
        await using (var conn = new NpgsqlConnection(Fixture.ConnectionString))
            await conn.ExecuteAsync("UPDATE users SET is_admin = TRUE WHERE username = @username", new { username });

        var login = await client.PostJsonAsync("/auth/login", new { username, password = Password }, TestData.NewIp());
        return (await login.ReadStringPropAsync("session"))!;
    }
}
