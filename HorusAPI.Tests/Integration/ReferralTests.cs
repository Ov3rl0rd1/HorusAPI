using System.Net;
using Dapper;
using HorusAPI.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HorusAPI.Tests.Integration;

/// <summary>
/// The referral programme end to end: the admin appoints a partner, a customer arrives with the
/// code (sign-up link or checkout field), pays less, and the partner earns a share of every ruble
/// that actually lands — and loses it again on a refund. Money is driven through the real
/// checkout and the provider webhook (<see cref="FakePaymentProvider"/>), not written by hand.
/// </summary>
public class ReferralTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    // ── Appointing partners ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Admin_makes_a_partner_and_lists_them()
    {
        RequireDb();
        var client = Client();
        string admin = await AdminSessionAsync(client);
        var (partner, _, _) = await RegisterVerifiedUserAsync(client);
        string code = NewCode();

        var put = await client.PutJsonAsync($"/admin/users/{partner}/referral",
            new { code, discount_percent = 10, reward_percent = 30, note = "блогер" }, TestData.NewIp(), admin);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var item = await put.ReadJsonAsync();
        Assert.Equal(code, item.GetProperty("code").GetString());
        Assert.Equal(0, item.GetProperty("balance").GetInt32());

        var list = await (await client.GetWithAsync("/admin/referrals", TestData.NewIp(), admin)).ReadJsonAsync();
        Assert.Contains(list.EnumerateArray(), p => p.GetProperty("username").GetString() == partner);
    }

    [SkippableFact]
    public async Task Only_an_admin_may_appoint_a_partner()
    {
        RequireDb();
        var client = Client();
        var (_, _, session) = await RegisterVerifiedUserAsync(client);
        var (other, _, _) = await RegisterVerifiedUserAsync(client);

        var res = await client.PutJsonAsync($"/admin/users/{other}/referral",
            new { code = NewCode(), discount_percent = 10, reward_percent = 30 }, TestData.NewIp(), session);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [SkippableFact]
    public async Task Partner_and_promo_codes_share_one_namespace()
    {
        RequireDb();
        var client = Client();
        string admin = await AdminSessionAsync(client);
        var (partner, _, _) = await RegisterVerifiedUserAsync(client);
        string code = await MakePartnerAsync(client, admin, partner, discount: 10, reward: 30);

        // A promo cannot take a partner's code (different case included)…
        var promo = await client.PostJsonAsync("/admin/promocodes",
            new { code = code.ToLowerInvariant(), percent_off = 50 }, TestData.NewIp(), admin);
        Assert.Equal(HttpStatusCode.Conflict, promo.StatusCode);

        // …nor a partner a promo's.
        string promoCode = "PROMO" + Guid.NewGuid().ToString("N")[..6];
        Assert.Equal(HttpStatusCode.Created, (await client.PostJsonAsync("/admin/promocodes",
            new { code = promoCode, percent_off = 50 }, TestData.NewIp(), admin)).StatusCode);
        var (second, _, _) = await RegisterVerifiedUserAsync(client);
        var clash = await client.PutJsonAsync($"/admin/users/{second}/referral",
            new { code = promoCode, discount_percent = 10, reward_percent = 30 }, TestData.NewIp(), admin);
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal("code_taken", await clash.ReadStringPropAsync("code"));
    }

    // ── Arriving with a code ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Sign_up_link_binds_the_customer_and_their_one_time_purchase_pays_the_partner()
    {
        RequireDb();
        await SeedServerAsync();
        string plan = await SeedPlanAsync("one_time", 300);
        var client = Client();
        string admin = await AdminSessionAsync(client);
        var (partner, _, partnerSession) = await RegisterVerifiedUserAsync(client);
        string code = await MakePartnerAsync(client, admin, partner, discount: 10, reward: 30);

        var (customer, session, referral) = await RegisterWithCodeAsync(client, code);
        Assert.Equal("applied", referral);

        // 10% off, computed by the server.
        var checkout = await client.PostJsonAsync("/billing/checkout", new { plan_code = plan }, TestData.NewIp(), session);
        Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);
        var view = await checkout.ReadJsonAsync();
        Assert.Equal(270, view.GetProperty("amount").GetInt32());
        Assert.Equal(30, view.GetProperty("discount").GetInt32());

        await ConfirmOneTimeAsync(client, Fixture.Payments.LastRef!, 270);

        // 30% of what was actually paid, rounded down: 81.
        var mine = await (await client.GetWithAsync("/billing/referral", TestData.NewIp(), partnerSession)).ReadJsonAsync();
        var self = mine.GetProperty("partner");
        Assert.Equal(1, self.GetProperty("invited").GetInt32());
        Assert.Equal(1, self.GetProperty("paying").GetInt32());
        Assert.Equal(81, self.GetProperty("earned").GetInt32());
        Assert.Equal(81, self.GetProperty("balance").GetInt32());
        Assert.Contains($"ref={code}", self.GetProperty("link").GetString());

        // The customer sees the discount they came with.
        var theirs = await (await client.GetWithAsync("/billing/referral", TestData.NewIp(), session)).ReadJsonAsync();
        Assert.Equal(10, theirs.GetProperty("invited").GetProperty("discount_percent").GetInt32());
        Assert.Equal(JsonValueKindNull, theirs.GetProperty("partner").ValueKind);

        // A replayed webhook does not pay twice.
        await ConfirmOneTimeAsync(client, Fixture.Payments.LastRef!, 270);
        Assert.Equal(81, await EarnedAsync(partner));
        Assert.Equal(customer, await ReferredCustomerAsync(partner));
    }

    [SkippableFact]
    public async Task An_unknown_code_at_sign_up_still_creates_the_account()
    {
        RequireDb();
        var client = Client();

        var (_, session, referral) = await RegisterWithCodeAsync(client, "NOSUCHCODE");

        Assert.Equal("invalid", referral);
        Assert.False(string.IsNullOrEmpty(session));
    }

    [SkippableFact]
    public async Task Code_in_the_checkout_field_gives_a_permanent_discount_on_a_subscription()
    {
        RequireDb();
        await SeedServerAsync();
        string plan = await SeedPlanAsync("recurring", 249);
        var client = Client();
        string admin = await AdminSessionAsync(client);
        var (partner, _, _) = await RegisterVerifiedUserAsync(client);
        string code = await MakePartnerAsync(client, admin, partner, discount: 10, reward: 30);
        var (_, _, session) = await RegisterVerifiedUserAsync(client);

        // A promo would be refused on a subscription; a partner's code is not a promo.
        var checkout = await client.PostJsonAsync("/billing/checkout",
            new { plan_code = plan, promo_code = code.ToLowerInvariant() }, TestData.NewIp(), session);
        Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);
        Assert.Equal(224, (await checkout.ReadJsonAsync()).GetProperty("amount").GetInt32());

        string subRef = Fixture.Payments.LastRef!;
        var periodEnd = DateTime.UtcNow.AddMonths(1);
        await ActivateAsync(client, subRef, periodEnd);

        // The checkout's payment is confirmed by the activation (the sweeper used to fail it),
        // and the first period pays the partner 30% of 224.
        Assert.Equal("confirmed", await PaymentStatusAsync(subRef));
        Assert.Equal(67, await EarnedAsync(partner));

        // A provider reporting that same first charge again pays nothing more…
        await ChargeAsync(client, subRef, "txn-" + Guid.NewGuid().ToString("N")[..8], 224, periodEnd);
        Assert.Equal(67, await EarnedAsync(partner));

        // …while a real renewal, for the next period, pays again.
        await ChargeAsync(client, subRef, "txn-" + Guid.NewGuid().ToString("N")[..8], 224, periodEnd.AddMonths(1));
        Assert.Equal(134, await EarnedAsync(partner));
    }

    [SkippableFact]
    public async Task A_partner_cannot_refer_themselves()
    {
        RequireDb();
        await SeedServerAsync();
        string plan = await SeedPlanAsync("one_time", 300);
        var client = Client();
        string admin = await AdminSessionAsync(client);
        var (partner, _, partnerSession) = await RegisterVerifiedUserAsync(client);
        string code = await MakePartnerAsync(client, admin, partner, discount: 50, reward: 50);

        var res = await client.PostJsonAsync("/billing/checkout",
            new { plan_code = plan, promo_code = code }, TestData.NewIp(), partnerSession);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("referral_not_applicable", await res.ReadStringPropAsync("code"));
    }

    [SkippableFact]
    public async Task An_existing_customer_cannot_attach_to_a_partner()
    {
        RequireDb();
        await SeedServerAsync();
        string plan = await SeedPlanAsync("one_time", 300);
        var client = Client();
        string admin = await AdminSessionAsync(client);
        var (partner, _, _) = await RegisterVerifiedUserAsync(client);
        string code = await MakePartnerAsync(client, admin, partner, discount: 10, reward: 30);

        // Paid once without a code.
        var (_, _, session) = await RegisterVerifiedUserAsync(client);
        await client.PostJsonAsync("/billing/checkout", new { plan_code = plan }, TestData.NewIp(), session);
        await ConfirmOneTimeAsync(client, Fixture.Payments.LastRef!, 300);

        var res = await client.PostJsonAsync("/billing/checkout",
            new { plan_code = plan, promo_code = code }, TestData.NewIp(), session);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("referral_not_applicable", await res.ReadStringPropAsync("code"));
        Assert.Contains("existing_customer", await res.ReadStringPropAsync("message"));
    }

    [SkippableFact]
    public async Task The_first_code_wins()
    {
        RequireDb();
        await SeedServerAsync();
        string plan = await SeedPlanAsync("one_time", 300);
        var client = Client();
        string admin = await AdminSessionAsync(client);
        var (first, _, _) = await RegisterVerifiedUserAsync(client);
        var (second, _, _) = await RegisterVerifiedUserAsync(client);
        string firstCode = await MakePartnerAsync(client, admin, first, discount: 10, reward: 30);
        string secondCode = await MakePartnerAsync(client, admin, second, discount: 20, reward: 30);

        var (_, session, _) = await RegisterWithCodeAsync(client, firstCode);

        var res = await client.PostJsonAsync("/billing/checkout",
            new { plan_code = plan, promo_code = secondCode }, TestData.NewIp(), session);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        // Re-entering the code they already have is fine, and changes nothing.
        var same = await client.PostJsonAsync("/billing/checkout",
            new { plan_code = plan, promo_code = firstCode }, TestData.NewIp(), session);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(270, (await same.ReadJsonAsync()).GetProperty("amount").GetInt32());
    }

    [SkippableFact]
    public async Task The_bigger_of_promo_and_referral_discount_applies_and_a_losing_promo_is_not_spent()
    {
        RequireDb();
        await SeedServerAsync();
        string plan = await SeedPlanAsync("one_time", 300);
        var client = Client();
        string admin = await AdminSessionAsync(client);
        var (partner, _, _) = await RegisterVerifiedUserAsync(client);
        string code = await MakePartnerAsync(client, admin, partner, discount: 20, reward: 30);
        string smallPromo = await SeedPromoAsync(10);
        string bigPromo = await SeedPromoAsync(50);
        var (_, session, _) = await RegisterWithCodeAsync(client, code);

        var lose = await client.PostJsonAsync("/billing/checkout",
            new { plan_code = plan, promo_code = smallPromo }, TestData.NewIp(), session);
        Assert.Equal(240, (await lose.ReadJsonAsync()).GetProperty("amount").GetInt32());   // referral 20%
        await ConfirmOneTimeAsync(client, Fixture.Payments.LastRef!, 240);
        Assert.Equal(0, await RedemptionsAsync(smallPromo));

        var win = await client.PostJsonAsync("/billing/checkout",
            new { plan_code = plan, promo_code = bigPromo }, TestData.NewIp(), session);
        Assert.Equal(150, (await win.ReadJsonAsync()).GetProperty("amount").GetInt32());    // promo 50%
        await ConfirmOneTimeAsync(client, Fixture.Payments.LastRef!, 150);
        Assert.Equal(1, await RedemptionsAsync(bigPromo));

        // The partner earns on both, whichever discount was used: 30% of 240 + 30% of 150.
        Assert.Equal(72 + 45, await EarnedAsync(partner));
    }

    // ── Money going back, and paying partners ────────────────────────────────────

    [SkippableFact]
    public async Task A_refund_takes_the_partners_share_back()
    {
        RequireDb();
        await SeedServerAsync();
        string plan = await SeedPlanAsync("one_time", 300);
        var client = Client();
        string admin = await AdminSessionAsync(client);
        var (partner, _, _) = await RegisterVerifiedUserAsync(client);
        string code = await MakePartnerAsync(client, admin, partner, discount: 10, reward: 30);
        var (_, session, _) = await RegisterWithCodeAsync(client, code);

        await client.PostJsonAsync("/billing/checkout", new { plan_code = plan }, TestData.NewIp(), session);
        string reff = Fixture.Payments.LastRef!;
        await ConfirmOneTimeAsync(client, reff, 270);
        Assert.Equal(81, await EarnedAsync(partner));

        int paymentId = await PaymentIdAsync(reff);
        var refund = await client.PostJsonAsync($"/admin/payments/{paymentId}/refund", new { reason = "test" }, TestData.NewIp(), admin);
        Assert.Equal(HttpStatusCode.OK, refund.StatusCode);

        Assert.Equal(0, await EarnedAsync(partner));
        var detail = await (await client.GetWithAsync($"/admin/referrals/{partner}", TestData.NewIp(), admin)).ReadJsonAsync();
        Assert.Equal("reversed", detail.GetProperty("rewards")[0].GetProperty("status").GetString());
    }

    [SkippableFact]
    public async Task Payouts_come_off_the_balance_and_cannot_exceed_it()
    {
        RequireDb();
        await SeedServerAsync();
        string plan = await SeedPlanAsync("one_time", 300);
        var client = Client();
        string admin = await AdminSessionAsync(client);
        var (partner, _, _) = await RegisterVerifiedUserAsync(client);
        string code = await MakePartnerAsync(client, admin, partner, discount: 10, reward: 30);
        var (_, session, _) = await RegisterWithCodeAsync(client, code);
        await client.PostJsonAsync("/billing/checkout", new { plan_code = plan }, TestData.NewIp(), session);
        await ConfirmOneTimeAsync(client, Fixture.Payments.LastRef!, 270);   // balance 81

        var tooMuch = await client.PostJsonAsync($"/admin/users/{partner}/referral/payouts",
            new { amount = 82 }, TestData.NewIp(), admin);
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);

        var paid = await client.PostJsonAsync($"/admin/users/{partner}/referral/payouts",
            new { amount = 50, note = "СБП" }, TestData.NewIp(), admin);
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        var item = await paid.ReadJsonAsync();
        Assert.Equal(50, item.GetProperty("paid_out").GetInt32());
        Assert.Equal(31, item.GetProperty("balance").GetInt32());
    }

    [SkippableFact]
    public async Task A_switched_off_partner_gives_no_discount_and_earns_nothing_new()
    {
        RequireDb();
        await SeedServerAsync();
        string plan = await SeedPlanAsync("one_time", 300);
        var client = Client();
        string admin = await AdminSessionAsync(client);
        var (partner, _, _) = await RegisterVerifiedUserAsync(client);
        string code = await MakePartnerAsync(client, admin, partner, discount: 10, reward: 30);
        var (_, session, _) = await RegisterWithCodeAsync(client, code);

        var off = await client.PutJsonAsync($"/admin/users/{partner}/referral", new { is_active = false }, TestData.NewIp(), admin);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await off.ReadJsonAsync()).GetProperty("is_active").GetBoolean());

        var checkout = await client.PostJsonAsync("/billing/checkout", new { plan_code = plan }, TestData.NewIp(), session);
        Assert.Equal(300, (await checkout.ReadJsonAsync()).GetProperty("amount").GetInt32());
        await ConfirmOneTimeAsync(client, Fixture.Payments.LastRef!, 300);

        Assert.Equal(0, await EarnedAsync(partner));
    }

    [SkippableFact]
    public async Task The_sweeper_spares_an_unverified_partner()
    {
        RequireDb();
        var client = Client();
        string admin = await AdminSessionAsync(client);
        string username = TestData.NewUsername();
        string email = TestData.NewEmail();
        await client.PostJsonAsync("/auth/register", new { username, password = Password, email }, TestData.NewIp());
        await MakePartnerAsync(client, admin, username, discount: 10, reward: 30);

        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.ExecuteAsync("UPDATE users SET created_at = NOW() - INTERVAL '30 days' WHERE username = @username", new { username });

        using (var scope = Fixture.Factory!.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<HorusAPI.Services.IAccountService>()
                .DeleteStaleUnverifiedAsync(TimeSpan.FromDays(7));

        // Every FK into users cascades: deleting this row would erase a partner and its history.
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users WHERE username = @username", new { username }));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static readonly System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    private static string NewCode() => "REF" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    private static async Task<string> MakePartnerAsync(HttpClient client, string admin, string username, int discount, int reward)
    {
        string code = NewCode();
        var res = await client.PutJsonAsync($"/admin/users/{username}/referral",
            new { code, discount_percent = discount, reward_percent = reward }, TestData.NewIp(), admin);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return code;
    }

    /// <summary>Sign up through a partner's link and confirm the address.</summary>
    private async Task<(string username, string session, string? referral)> RegisterWithCodeAsync(HttpClient client, string code)
    {
        string username = TestData.NewUsername();
        string email = TestData.NewEmail();
        string ip = TestData.NewIp();

        var register = await client.PostJsonAsync("/auth/register",
            new { username, password = Password, email, referral_code = code }, ip);
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        string? referral = await register.ReadStringPropAsync("referral");

        string verifyCode = Fixture.Email.LastCodeFor(email)!;
        var verify = await client.PostJsonAsync("/auth/verify", new { email, code = verifyCode }, ip);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        return (username, (await verify.ReadStringPropAsync("session"))!, referral);
    }

    private static Task ConfirmOneTimeAsync(HttpClient client, string reff, int amount) =>
        PostWebhookAsync(client, new { id = reff, amount, currency = "RUB", status = "CONFIRMED" });

    private static Task ActivateAsync(HttpClient client, string subRef, DateTime periodEnd) => PostWebhookAsync(client, new
    {
        Id = subRef, Amount = 224, Currency = "RUB", Status = "SUBSCRIPTION_ACTIVATED",
        PaymentMethod = 6, SubscriptionId = subRef, NextChargeAt = periodEnd
    });

    private static Task ChargeAsync(HttpClient client, string subRef, string txn, int amount, DateTime nextChargeAt) =>
        PostWebhookAsync(client, new
        {
            Id = txn, Amount = amount, Currency = "RUB", Status = "CONFIRMED",
            SubscriptionId = subRef, NextChargeAt = nextChargeAt
        });

    private static async Task PostWebhookAsync(HttpClient client, object body)
    {
        var res = await client.PostJsonAsync("/payments/platega/webhook", body, TestData.NewIp());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    private async Task<int> EarnedAsync(string partner)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<int>("""
            SELECT COALESCE(SUM(r.amount), 0)::int FROM referral_rewards r
            JOIN users u ON u.id = r.partner_id
            WHERE u.username = @partner AND r.status = 'accrued'
            """, new { partner });
    }

    private async Task<string?> ReferredCustomerAsync(string partner)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<string?>("""
            SELECT c.username FROM users c JOIN users p ON p.id = c.referred_by WHERE p.username = @partner LIMIT 1
            """, new { partner });
    }

    private async Task<string?> PaymentStatusAsync(string reff)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<string?>("SELECT status FROM payments WHERE provider_ref = @reff", new { reff });
    }

    private async Task<int> PaymentIdAsync(string reff)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<int>("SELECT id FROM payments WHERE provider_ref = @reff", new { reff });
    }

    private async Task<int> RedemptionsAsync(string promo)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)::int FROM promo_redemptions r JOIN promo_codes p ON p.id = r.promo_code_id WHERE p.code = @promo
            """, new { promo });
    }

    private async Task<string> SeedPlanAsync(string kind, int amount)
    {
        string code = "plan_" + Guid.NewGuid().ToString("N")[..10];
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.ExecuteAsync("""
            INSERT INTO plans (code, title, tier, kind, interval_unit, interval_count, amount, is_public)
            VALUES (@code, @code, 'standard', @kind, 'month', 1, @amount, TRUE)
            """, new { code, kind, amount });
        return code;
    }

    private async Task<string> SeedPromoAsync(int percent)
    {
        string code = "promo_" + Guid.NewGuid().ToString("N")[..8];
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.ExecuteAsync("INSERT INTO promo_codes (code, kind, percent_off, is_active) VALUES (@code, 'percent', @pct, TRUE)",
            new { code, pct = (short)percent });
        return code;
    }

    private async Task SeedServerAsync()
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.ExecuteAsync("""
            INSERT INTO vpn_servers (name, country, city, host, max_clients, max_reservations, auth_password, is_active)
            VALUES ('T', 'TT', 'TC', @host, 50, 50, 'pw', TRUE)
            """, new { host = "node-" + Guid.NewGuid().ToString("N")[..8] + ".example" });
    }
}
