using System.Net;
using Dapper;
using HorusAPI.Tests.Infrastructure;
using Npgsql;

namespace HorusAPI.Tests.Integration;

/// <summary>
/// The API side of the site's admin panel: the route nginx gates the panel pages on, and
/// the two reads the panel needs that the admin API did not have — finding a user, and
/// seeing who a payment belongs to and when it was made.
/// </summary>
public class AdminPanelTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    // ── The gate ─────────────────────────────────────────────────────────────
    // nginx turns 401 and 403 into a 404, and passes the file on a 2xx. Anything else is
    // an nginx 500 — so these three codes are the whole contract, and a 200 with a body
    // or a 404 from here would break it in different ways.

    [SkippableFact]
    public async Task Gate_is_401_without_a_session()
    {
        RequireDb();
        var res = await Client().GetWithAsync("/admin/gate", TestData.NewIp(), session: null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [SkippableFact]
    public async Task Gate_is_403_for_an_ordinary_user()
    {
        RequireDb();
        var client = Client();
        var (_, _, session) = await RegisterVerifiedUserAsync(client);

        var res = await client.GetWithAsync("/admin/gate", TestData.NewIp(), session);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [SkippableFact]
    public async Task Gate_is_204_for_an_admin()
    {
        RequireDb();
        var client = Client();
        var (admin, _) = await NewAdminAsync(client);

        var res = await client.GetWithAsync("/admin/gate", TestData.NewIp(), admin);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    // ── User search ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Users_are_found_by_part_of_the_username()
    {
        RequireDb();
        var client = Client();
        var (admin, _) = await NewAdminAsync(client);
        var (username, _, _) = await RegisterVerifiedUserAsync(client);

        var found = await SearchAsync(client, admin, username[2..^2]);

        Assert.Contains(username, found);
    }

    [SkippableFact]
    public async Task Users_are_found_by_part_of_the_email()
    {
        RequireDb();
        var client = Client();
        var (admin, _) = await NewAdminAsync(client);
        var (username, email, _) = await RegisterVerifiedUserAsync(client);

        var found = await SearchAsync(client, admin, email.Split('@')[0].ToUpperInvariant());

        Assert.Contains(username, found);
    }

    [SkippableFact]
    public async Task Users_are_found_by_id()
    {
        RequireDb();
        var client = Client();
        var (admin, _) = await NewAdminAsync(client);
        var (username, _, _) = await RegisterVerifiedUserAsync(client);
        var id = await UserIdAsync(username);

        var found = await SearchAsync(client, admin, id.ToString());

        Assert.Contains(username, found);
    }

    [SkippableFact]
    public async Task An_underscore_in_the_query_is_a_character_not_a_wildcard()
    {
        RequireDb();
        var client = Client();
        var (admin, _) = await NewAdminAsync(client);

        // Two addresses that differ only where one has '_'. As a LIKE pattern, "a_b" also
        // matches "aXb" — so an admin looking up one person would be shown the other.
        var tag = Guid.NewGuid().ToString("N")[..10];
        var withUnderscore = await InsertUserAsync($"u{tag}a", $"{tag}_x@example.test");
        var lookalike      = await InsertUserAsync($"u{tag}b", $"{tag}Qx@example.test");

        var found = await SearchAsync(client, admin, $"{tag}_x");

        Assert.Contains(withUnderscore, found);
        Assert.DoesNotContain(lookalike, found);
    }

    [SkippableFact]
    public async Task User_search_is_admin_only()
    {
        RequireDb();
        var client = Client();
        var (_, _, session) = await RegisterVerifiedUserAsync(client);

        var res = await client.GetWithAsync("/admin/users?q=a", TestData.NewIp(), session);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ── Reads the panel depends on ───────────────────────────────────────────

    [SkippableFact]
    public async Task Payments_carry_the_username_and_the_date()
    {
        RequireDb();
        var client = Client();
        var (admin, _) = await NewAdminAsync(client);
        var (username, _, _) = await RegisterVerifiedUserAsync(client);

        // Straight into the table: the point is the listing, not the checkout path. It also
        // exercises the constructor binding — classic Dapper matches a positional record
        // column by column, and a query whose order drifts from the record's fails at
        // runtime, as a 503, with nothing at build time to say so.
        await using (var conn = new NpgsqlConnection(Fixture.ConnectionString))
            await conn.ExecuteAsync("""
                INSERT INTO payments (user_id, provider, kind, amount, status)
                SELECT id, 'test', 'one_time', 299, 'confirmed' FROM users WHERE username = @username
                """, new { username });

        var res = await client.GetWithAsync($"/admin/payments?user={username}", TestData.NewIp(), admin);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var row = Assert.Single((await res.ReadJsonAsync()).EnumerateArray());
        Assert.Equal(username, row.GetProperty("username").GetString());
        Assert.Equal(299, row.GetProperty("amount").GetInt32());
        Assert.True(row.GetProperty("created_at").GetDateTime() > DateTime.UtcNow.AddMinutes(-5));
    }

    [SkippableFact]
    public async Task Servers_carry_the_reservation_count()
    {
        RequireDb();
        var client = Client();
        var (admin, _) = await NewAdminAsync(client);

        int id;
        await using (var conn = new NpgsqlConnection(Fixture.ConnectionString))
            id = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO vpn_servers (name, country, city, host, max_clients, max_reservations, reserved_count, auth_password)
                VALUES ('P', 'PP', 'PC', @host, 4, 6, 5, 'pw')
                RETURNING id
                """, new { host = "panel-" + Guid.NewGuid().ToString("N")[..8] + ".example" });

        var res = await client.GetWithAsync("/admin/servers", TestData.NewIp(), admin);
        var server = (await res.ReadJsonAsync()).EnumerateArray().Single(s => s.GetProperty("id").GetInt32() == id);

        // reserved_count is what capacity is measured on; without it the panel could only
        // show the live online count, which says nothing about whether a node is full.
        Assert.Equal(5, server.GetProperty("reserved_count").GetInt32());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string[]> SearchAsync(HttpClient client, string admin, string q)
    {
        var res = await client.GetWithAsync("/admin/users?q=" + Uri.EscapeDataString(q), TestData.NewIp(), admin);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        return [.. (await res.ReadJsonAsync()).EnumerateArray().Select(u => u.GetProperty("username").GetString()!)];
    }

    private async Task<string> InsertUserAsync(string username, string email)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.ExecuteAsync(
            "INSERT INTO users (username, password_hash, email) VALUES (@username, 'x', @email)",
            new { username, email });
        return username;
    }

    private async Task<int> UserIdAsync(string username)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<int>("SELECT id FROM users WHERE username = @username", new { username });
    }

    private async Task<(string session, string username)> NewAdminAsync(HttpClient client)
    {
        var (username, _, _) = await RegisterVerifiedUserAsync(client);

        await using (var conn = new NpgsqlConnection(Fixture.ConnectionString))
            await conn.ExecuteAsync("UPDATE users SET is_admin = TRUE WHERE username = @username", new { username });

        // A fresh login, so the session carries the Admin role.
        var login = await client.PostJsonAsync("/auth/login", new { username, password = Password }, TestData.NewIp());
        return ((await login.ReadStringPropAsync("session"))!, username);
    }
}
