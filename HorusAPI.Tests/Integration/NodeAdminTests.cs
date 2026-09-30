using System.Net;
using Dapper;
using HorusAPI.Tests.Infrastructure;
using Npgsql;

namespace HorusAPI.Tests.Integration;

/// <summary>
/// Moving ONE user off a node, and the node detail view the panel shows them in.
///
/// <para>The per-user move differs from evacuation in the one way that matters: the node stays
/// in rotation. So the auto-picker has to be told to skip it — with a seat just freed it is the
/// least-loaded node, and without that the "move" would put the user straight back.</para>
/// </summary>
public class NodeAdminTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    [SkippableFact]
    public async Task Moving_a_user_takes_them_off_the_node_and_leaves_it_in_rotation()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);

        var from = await SeedServerAsync();
        await SeedServerAsync();
        var (userId, _, _) = await BindUserAsync(client, from);

        var res = await MoveAsync(client, admin, from, userId, new { });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var now = await CurrentServerAsync(userId);
        Assert.NotEqual(from, now);
        Assert.Equal(now, (await res.ReadJsonAsync()).GetProperty("server_id").GetInt32());
        Assert.True(await IsActiveAsync(from));
        Assert.Equal(0, await ReservedAsync(from));
    }

    [SkippableFact]
    public async Task The_user_is_provisioned_on_the_new_node_and_removed_from_the_old()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);

        var from = await SeedServerAsync();
        var to   = await SeedServerAsync();
        var (userId, _, _) = await BindUserAsync(client, from);

        var res = await MoveAsync(client, admin, from, userId, new { server_id = to });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Empty((await res.ReadJsonAsync()).GetProperty("problems").EnumerateArray());

        var calls = Fixture.Nodes.For(await UuidAsync(userId)).ToList();
        var (toHost, fromHost) = (await HostAsync(to), await HostAsync(from));
        Assert.Contains(calls, c => c.Op == "add"    && c.Host == toHost);
        Assert.Contains(calls, c => c.Op == "remove" && c.Host == fromHost);
    }

    [SkippableFact]
    public async Task An_unreachable_target_is_reported_but_the_move_stands()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);

        var from = await SeedServerAsync();
        var down = await SeedServerAsync(hostPrefix: "down-");
        var (userId, _, _) = await BindUserAsync(client, from);

        var res = await MoveAsync(client, admin, from, userId, new { server_id = down });

        // The database is the truth and the node reconciles from it, so the binding is not
        // rolled back — but the admin has to be told the user cannot connect yet.
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.NotEmpty((await res.ReadJsonAsync()).GetProperty("problems").EnumerateArray());
        Assert.Equal(down, await CurrentServerAsync(userId));
    }

    [SkippableFact]
    public async Task The_auto_pick_never_hands_the_user_back_to_the_node_they_left()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);

        // After the move frees its seat the source is at 0/5 and the other node at 3/5, so a
        // least-loaded pick that did not exclude the source would choose it.
        var from  = await SeedServerAsync();
        var other = await SeedServerAsync(reserved: 3);
        var (userId, _, _) = await BindUserAsync(client, from);

        var reactivate = await DeactivateServersExceptAsync(from, other);
        try
        {
            var res = await MoveAsync(client, admin, from, userId, new { });

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal(other, await CurrentServerAsync(userId));
        }
        finally { await ReactivateAsync(reactivate); }
    }

    [SkippableFact]
    public async Task With_nowhere_to_go_the_move_is_refused_and_the_user_stays()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);

        var only = await SeedServerAsync();
        var (userId, _, _) = await BindUserAsync(client, only);

        var reactivate = await DeactivateServersExceptAsync(only);
        try
        {
            var res = await MoveAsync(client, admin, only, userId, new { });

            // Unlike /servers/select, "nothing has room" must not come back as a success
            // that quietly kept the old binding.
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Equal("no_capacity", await res.ReadStringPropAsync("code"));
            Assert.Equal(only, await CurrentServerAsync(userId));
            Assert.Equal(1, await ReservedAsync(only));
        }
        finally { await ReactivateAsync(reactivate); }
    }

    [SkippableFact]
    public async Task A_chosen_target_is_used_and_both_counters_move()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);

        var from = await SeedServerAsync();
        var to   = await SeedServerAsync();
        var (userId, _, _) = await BindUserAsync(client, from);

        var res = await MoveAsync(client, admin, from, userId, new { server_id = to });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(to, await CurrentServerAsync(userId));
        Assert.Equal(0, await ReservedAsync(from));
        Assert.Equal(1, await ReservedAsync(to));
    }

    [SkippableFact]
    public async Task A_full_target_is_refused()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);

        var from = await SeedServerAsync();
        var full = await SeedServerAsync(max: 1, reserved: 1);
        var (userId, _, _) = await BindUserAsync(client, from);

        var res = await MoveAsync(client, admin, from, userId, new { server_id = full });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("no_capacity", await res.ReadStringPropAsync("code"));
        Assert.Equal(from, await CurrentServerAsync(userId));
    }

    [SkippableFact]
    public async Task A_target_out_of_rotation_is_refused()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);

        var from     = await SeedServerAsync();
        var inactive = await SeedServerAsync(active: false);
        var (userId, _, _) = await BindUserAsync(client, from);

        var res = await MoveAsync(client, admin, from, userId, new { server_id = inactive });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("target_inactive", await res.ReadStringPropAsync("code"));
    }

    [SkippableFact]
    public async Task Moving_to_the_same_node_is_a_400()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);

        var from = await SeedServerAsync();
        var (userId, _, _) = await BindUserAsync(client, from);

        var res = await MoveAsync(client, admin, from, userId, new { server_id = from });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("same_server", await res.ReadStringPropAsync("code"));
    }

    [SkippableFact]
    public async Task A_user_who_is_not_on_that_node_is_left_alone()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);

        // The admin's view is stale: the user has since moved to another node. Moving them
        // "off" the old one must not take them off the one they chose.
        var stale  = await SeedServerAsync();
        var theirs = await SeedServerAsync();
        var (userId, _, _) = await BindUserAsync(client, theirs);

        var res = await MoveAsync(client, admin, stale, userId, new { });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("not_on_server", await res.ReadStringPropAsync("code"));
        Assert.Equal(theirs, await CurrentServerAsync(userId));
    }

    [SkippableFact]
    public async Task An_unknown_user_is_a_404()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);
        var from = await SeedServerAsync();

        var res = await MoveAsync(client, admin, from, 999_999, new { });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [SkippableFact]
    public async Task The_moved_user_sees_the_new_node_at_once()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);

        var from = await SeedServerAsync();
        var to   = await SeedServerAsync();
        var (userId, _, session) = await BindUserAsync(client, from);

        // Warm the session cache with the old binding.
        var before = await client.GetWithAsync("/whoami", TestData.NewIp(), session);
        Assert.Equal(from, (await before.ReadJsonAsync()).GetProperty("currentServerId").GetInt32());

        await MoveAsync(client, admin, from, userId, new { server_id = to });

        // Without eviction the cached User would keep answering with the old node for hours.
        var after = await client.GetWithAsync("/whoami", TestData.NewIp(), session);
        Assert.Equal(to, (await after.ReadJsonAsync()).GetProperty("currentServerId").GetInt32());
    }

    [SkippableFact]
    public async Task Moving_a_user_is_admin_only()
    {
        RequireDb();
        var client = Client();
        var from = await SeedServerAsync();
        var (userId, _, session) = await BindUserAsync(client, from);

        var res = await MoveAsync(client, session, from, userId, new { });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(from, await CurrentServerAsync(userId));
    }

    // ── Node detail ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Node_detail_lists_its_users_holds_and_offers()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);

        var id = await SeedServerAsync();
        var (_, username, _) = await BindUserAsync(client, id);

        await using (var conn = new NpgsqlConnection(Fixture.ConnectionString))
        {
            // A pending checkout on this node, and two offers as a node would report them.
            await conn.ExecuteAsync("""
                INSERT INTO slot_holds (user_id, server_id, expires_at)
                SELECT id, @id, NOW() + INTERVAL '10 minutes' FROM users WHERE username = @u;
                UPDATE vpn_servers SET reserved_count = reserved_count + 1,
                    offers = '[{"id":"vless","label":"VLESS","outbound":{"protocol":"vless"},"uri":"vless://${uuid}@h"},
                               {"id":"rtc","audience":["app"],"outbound":{"protocol":"olcrtc"}}]'::jsonb
                WHERE id = @id;
                """, new { id, u = (await RegisterVerifiedUserAsync(client)).username });
        }

        var res = await client.GetWithAsync($"/admin/servers/{id}", TestData.NewIp(), admin);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.ReadJsonAsync();

        var node = body.GetProperty("node");
        Assert.Equal(1, node.GetProperty("bound_users").GetInt32());
        Assert.Equal(1, node.GetProperty("pending_holds").GetInt32());
        Assert.Equal(2, node.GetProperty("reserved_count").GetInt32());

        var user = Assert.Single(body.GetProperty("users").EnumerateArray());
        Assert.Equal(username, user.GetProperty("username").GetString());

        var offers = body.GetProperty("offers").EnumerateArray().ToList();
        Assert.Equal(["vless", "olcrtc"], offers.Select(o => o.GetProperty("protocol").GetString()));
        Assert.True(offers[0].GetProperty("has_uri").GetBoolean());
        Assert.False(offers[1].GetProperty("has_uri").GetBoolean());
    }

    [SkippableFact]
    public async Task Node_detail_of_an_unknown_node_is_a_404()
    {
        RequireDb();
        var client = Client();
        var admin = await AdminSessionAsync(client);

        var res = await client.GetWithAsync("/admin/servers/999999", TestData.NewIp(), admin);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [SkippableFact]
    public async Task Node_detail_is_admin_only()
    {
        RequireDb();
        var client = Client();
        var (_, _, session) = await RegisterVerifiedUserAsync(client);
        var id = await SeedServerAsync();

        var res = await client.GetWithAsync($"/admin/servers/{id}", TestData.NewIp(), session);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> MoveAsync(HttpClient client, string session, int from, int userId, object body) =>
        client.PostJsonAsync($"/admin/servers/{from}/users/{userId}/evacuate", body, TestData.NewIp(), session);

    private async Task<int> SeedServerAsync(int max = 5, int reserved = 0, bool active = true, string hostPrefix = "move-")
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<int>("""
            INSERT INTO vpn_servers (name, country, city, host, max_clients, max_reservations, reserved_count, auth_password, is_active)
            VALUES ('M', 'MM', 'MC', @host, @max, @max, @reserved, 'pw', @active)
            RETURNING id
            """, new { host = hostPrefix + Guid.NewGuid().ToString("N")[..8] + ".example", max, reserved, active });
    }

    /// <summary>Registers a user and binds them to a node the way a purchase would.</summary>
    private async Task<(int id, string username, string session)> BindUserAsync(HttpClient client, int serverId)
    {
        var (username, _, session) = await RegisterVerifiedUserAsync(client);

        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.ExecuteAsync("""
            UPDATE users SET current_server_id = @s WHERE username = @u;
            UPDATE vpn_servers SET reserved_count = reserved_count + 1 WHERE id = @s;
            """, new { s = serverId, u = username });

        var id = await conn.ExecuteScalarAsync<int>("SELECT id FROM users WHERE username = @u", new { u = username });
        return (id, username, session);
    }

    /// <summary>
    /// Switches off every active server except <paramref name="keep"/> and returns what it
    /// touched. The database is shared across the collection, so "the fleet looks like this"
    /// has to be made true rather than assumed.
    /// </summary>
    private async Task<int[]> DeactivateServersExceptAsync(params int[] keep)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        var ids = (await conn.QueryAsync<int>(
            "SELECT id FROM vpn_servers WHERE is_active AND id <> ALL(@keep)", new { keep })).ToArray();
        if (ids.Length > 0)
            await conn.ExecuteAsync("UPDATE vpn_servers SET is_active = FALSE WHERE id = ANY(@ids)", new { ids });
        return ids;
    }

    private async Task ReactivateAsync(int[] ids)
    {
        if (ids.Length == 0) return;
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.ExecuteAsync("UPDATE vpn_servers SET is_active = TRUE WHERE id = ANY(@ids)", new { ids });
    }

    private async Task<int?> CurrentServerAsync(int userId)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<int?>("SELECT current_server_id FROM users WHERE id = @id", new { id = userId });
    }

    private async Task<string> HostAsync(int serverId)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<string>("SELECT host FROM vpn_servers WHERE id = @id", new { id = serverId }) ?? "";
    }

    private async Task<Guid> UuidAsync(int userId)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<Guid>("SELECT vpn_uuid FROM users WHERE id = @id", new { id = userId });
    }

    private async Task<int> ReservedAsync(int serverId)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<int>("SELECT reserved_count FROM vpn_servers WHERE id = @id", new { id = serverId });
    }

    private async Task<bool> IsActiveAsync(int serverId)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<bool>("SELECT is_active FROM vpn_servers WHERE id = @id", new { id = serverId });
    }
}
