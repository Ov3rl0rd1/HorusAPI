using System.Net;
using System.Text.Json;
using Dapper;
using HorusAPI.Tests.Infrastructure;
using Npgsql;

namespace HorusAPI.Tests.Integration;

/// <summary>
/// The tariff catalogue as the admin panel edits it, and who may buy a closed plan.
///
/// <para>The rule these guard hardest is the interval freeze: a renewal extends a period by the
/// plan's CURRENT interval while the provider keeps charging on the schedule the subscription
/// was created with, so an edit that changed it under a paying subscriber would quietly hand
/// out a year for the price of a month (or the reverse).</para>
/// </summary>
public class PlanAdminTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    [SkippableFact]
    public async Task The_admin_list_includes_closed_and_inactive_plans()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);

        var closed   = await SeedPlanAsync(isPublic: false);
        var inactive = await SeedPlanAsync(isActive: false);

        var codes = await AdminPlanCodesAsync(client, admin);

        Assert.Contains(closed, codes);
        Assert.Contains(inactive, codes);
    }

    [SkippableFact]
    public async Task A_created_public_plan_is_on_sale()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);
        var code = NewCode();

        var res = await client.PostJsonAsync("/admin/plans", PlanBody(code), TestData.NewIp(), admin);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Contains(code, await PublicPlanCodesAsync(client));
    }

    [SkippableFact]
    public async Task A_code_that_differs_only_in_case_is_a_409()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);
        var code = await SeedPlanAsync();

        // Checkout looks plans up by lower(code) … LIMIT 1, so both would answer to one code.
        var res = await client.PostJsonAsync("/admin/plans", PlanBody(code.ToUpperInvariant()), TestData.NewIp(), admin);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("plan_exists", await res.ReadStringPropAsync("code"));
    }

    [SkippableFact]
    public async Task An_invalid_plan_is_a_400()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);

        var res = await client.PostJsonAsync("/admin/plans", PlanBody(NewCode(), kind: "forever"), TestData.NewIp(), admin);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("invalid_plan", await res.ReadStringPropAsync("code"));
    }

    [SkippableFact]
    public async Task Editing_changes_the_plan_but_not_its_code()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);
        var code = await SeedPlanAsync();
        var id = await PlanIdAsync(code);

        var res = await client.PutJsonAsync($"/admin/plans/{id}",
            PlanBody("ignored", amount: 349, title: "Новое", isPublic: false), TestData.NewIp(), admin);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var plan = await res.ReadJsonAsync();
        Assert.Equal(code, plan.GetProperty("code").GetString());
        Assert.Equal(349, plan.GetProperty("amount").GetInt32());
        Assert.Equal("Новое", plan.GetProperty("title").GetString());
        Assert.False(plan.GetProperty("is_public").GetBoolean());
        Assert.DoesNotContain(code, await PublicPlanCodesAsync(client));
    }

    [SkippableFact]
    public async Task The_interval_is_frozen_while_a_subscription_renews_on_it()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);
        var code = await SeedPlanAsync();
        var id = await PlanIdAsync(code);
        await SeedSubscriptionAsync(client, id, "active");

        var interval = await client.PutJsonAsync($"/admin/plans/{id}",
            PlanBody(code, unit: "year"), TestData.NewIp(), admin);
        Assert.Equal(HttpStatusCode.Conflict, interval.StatusCode);
        Assert.Equal("plan_in_use", await interval.ReadStringPropAsync("code"));

        // The price is not frozen: it applies to new purchases.
        var price = await client.PutJsonAsync($"/admin/plans/{id}",
            PlanBody(code, amount: 999), TestData.NewIp(), admin);
        Assert.Equal(HttpStatusCode.OK, price.StatusCode);
    }

    [SkippableFact]
    public async Task The_interval_can_change_once_nothing_depends_on_it()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);
        var code = await SeedPlanAsync();
        var id = await PlanIdAsync(code);
        await SeedSubscriptionAsync(client, id, "failed");

        var res = await client.PutJsonAsync($"/admin/plans/{id}",
            PlanBody(code, unit: "year"), TestData.NewIp(), admin);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("year", (await res.ReadJsonAsync()).GetProperty("interval_unit").GetString());
    }

    [SkippableFact]
    public async Task Editing_an_unknown_plan_is_a_404()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);

        var res = await client.PutJsonAsync("/admin/plans/999999", PlanBody(NewCode()), TestData.NewIp(), admin);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ── Grants ───────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_grant_without_a_date_never_expires_and_opens_the_plan()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);
        var (username, _, session) = await RegisterVerifiedUserAsync(client);
        var code = await SeedPlanAsync(isPublic: false);

        var grant = await client.PostJsonAsync($"/admin/users/{username}/grant",
            new { plan_code = code, expires_at = (DateTime?)null }, TestData.NewIp(), admin);
        Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);

        var mine = await client.GetWithAsync($"/admin/users/{username}/grants", TestData.NewIp(), admin);
        var row = Assert.Single((await mine.ReadJsonAsync()).EnumerateArray());
        Assert.Equal(code, row.GetProperty("plan_code").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("expires_at").ValueKind);

        var byPlan = await client.GetWithAsync($"/admin/plans/{await PlanIdAsync(code)}/grants", TestData.NewIp(), admin);
        Assert.Contains(username, (await byPlan.ReadJsonAsync()).EnumerateArray().Select(g => g.GetProperty("username").GetString()));

        var plans = await client.GetWithAsync("/billing/plans", TestData.NewIp(), session);
        Assert.Contains(code, (await plans.ReadJsonAsync()).EnumerateArray().Select(p => p.GetProperty("code").GetString()));
    }

    [SkippableFact]
    public async Task Revoking_a_grant_hides_the_plan_again()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);
        var (username, _, session) = await RegisterVerifiedUserAsync(client);
        var code = await SeedPlanAsync(isPublic: false);

        await client.PostJsonAsync($"/admin/users/{username}/grant", new { plan_code = code }, TestData.NewIp(), admin);

        var revoke = await client.DeleteWithAsync($"/admin/users/{username}/grants/{code}", TestData.NewIp(), admin);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var plans = await client.GetWithAsync("/billing/plans", TestData.NewIp(), session);
        Assert.DoesNotContain(code, (await plans.ReadJsonAsync()).EnumerateArray().Select(p => p.GetProperty("code").GetString()));

        var again = await client.DeleteWithAsync($"/admin/users/{username}/grants/{code}", TestData.NewIp(), admin);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [SkippableFact]
    public async Task The_plan_list_counts_live_grants_only()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);
        var code = await SeedPlanAsync(isPublic: false);
        var (forever, _, _) = await RegisterVerifiedUserAsync(client);
        var (lapsed, _, _)  = await RegisterVerifiedUserAsync(client);

        await client.PostJsonAsync($"/admin/users/{forever}/grant", new { plan_code = code }, TestData.NewIp(), admin);
        await client.PostJsonAsync($"/admin/users/{lapsed}/grant",
            new { plan_code = code, expires_at = DateTime.UtcNow.AddDays(-1) }, TestData.NewIp(), admin);

        var res = await client.GetWithAsync("/admin/plans", TestData.NewIp(), admin);
        var plan = (await res.ReadJsonAsync()).EnumerateArray().Single(p => p.GetProperty("code").GetString() == code);

        Assert.Equal(1, plan.GetProperty("grants").GetInt32());
    }

    [SkippableFact]
    public async Task Grants_of_an_unknown_user_are_a_404()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);

        var res = await client.GetWithAsync("/admin/users/no_such_user_here/grants", TestData.NewIp(), admin);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [SkippableFact]
    public async Task Plan_admin_is_admin_only()
    {
        RequireDb();
        var client = Client();
        var (_, _, session) = await RegisterVerifiedUserAsync(client);

        var list   = await client.GetWithAsync("/admin/plans", TestData.NewIp(), session);
        var create = await client.PostJsonAsync("/admin/plans", PlanBody(NewCode()), TestData.NewIp(), session);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string NewCode() => "plan_" + Guid.NewGuid().ToString("N")[..10];

    private static object PlanBody(
        string code, string kind = "recurring", string unit = "month", int amount = 199,
        string title = "Месяц", bool isPublic = true) => new
    {
        code, title, tier = "standard", kind, interval_unit = unit, interval_count = 1, amount,
        is_public = isPublic, is_active = true
    };

    private async Task<string> SeedPlanAsync(bool isPublic = true, bool isActive = true)
    {
        string code = NewCode();
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.ExecuteAsync("""
            INSERT INTO plans (code, title, tier, kind, interval_unit, interval_count, amount, is_public, is_active)
            VALUES (@code, @code, 'standard', 'recurring', 'month', 1, 199, @isPublic, @isActive)
            """, new { code, isPublic, isActive });
        return code;
    }

    private async Task<int> PlanIdAsync(string code)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<int>("SELECT id FROM plans WHERE code = @code", new { code });
    }

    private async Task SeedSubscriptionAsync(HttpClient client, int planId, string status)
    {
        var (username, _, _) = await RegisterVerifiedUserAsync(client);
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.ExecuteAsync("""
            INSERT INTO subscriptions (user_id, plan_id, provider, kind, status, current_period_end)
            SELECT id, @planId, 'test', 'recurring', @status, NOW() + INTERVAL '20 days' FROM users WHERE username = @username
            """, new { planId, status, username });
    }

    private static async Task<List<string?>> AdminPlanCodesAsync(HttpClient client, string admin)
    {
        var res = await client.GetWithAsync("/admin/plans", TestData.NewIp(), admin);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return [.. (await res.ReadJsonAsync()).EnumerateArray().Select(p => p.GetProperty("code").GetString())];
    }

    private static async Task<List<string?>> PublicPlanCodesAsync(HttpClient client)
    {
        var res = await client.GetWithAsync("/billing/plans", TestData.NewIp(), session: null);
        return [.. (await res.ReadJsonAsync()).EnumerateArray().Select(p => p.GetProperty("code").GetString())];
    }
}
