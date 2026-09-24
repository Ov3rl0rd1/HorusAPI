using System.Net;
using Dapper;
using HorusAPI.Tests.Infrastructure;
using Npgsql;

namespace HorusAPI.Tests.Integration;

/// <summary>
/// Moving every user off a node.
///
/// <para>The reason it exists is an address being blocked: the node is healthy and reachable
/// from everywhere except where the users are, and all of them have to be somewhere else
/// within minutes. So what these test is not the happy path so much as the two ways it can
/// quietly do nothing useful — handing users back to the node they came from, and reporting
/// success for users it could not actually place.</para>
/// </summary>
public class EvacuationTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    [SkippableFact]
    public async Task Evacuating_moves_a_bound_user_to_another_node()
    {
        RequireDb();
        var client = Client();
        var admin = await NewAdminSessionAsync(client);

        var from = await SeedServerAsync();
        await SeedServerAsync();
        var userId = await BindUserAsync(client, from);

        var res = await client.PostJsonAsync($"/admin/servers/{from}/evacuate", new { }, TestData.NewIp(), admin);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.ReadJsonAsync();
        Assert.Equal(1, body.GetProperty("moved").GetInt32());
        Assert.Equal(0, body.GetProperty("stayed").GetInt32());

        // Off the old node is the invariant; WHICH node is the auto-picker's business and
        // depends on every server in the database, including ones other tests left behind —
        // the fixture shares one database across the collection. Asserting the destination
        // made this test a hostage to the order it happened to run in.
        Assert.NotEqual(from, await CurrentServerAsync(userId));
    }

    [SkippableFact]
    public async Task The_evacuated_node_is_taken_out_of_rotation_first()
    {
        RequireDb();
        var client = Client();
        var admin = await NewAdminSessionAsync(client);

        var from = await SeedServerAsync();
        await SeedServerAsync();
        await BindUserAsync(client, from);

        await client.PostJsonAsync($"/admin/servers/{from}/evacuate", new { }, TestData.NewIp(), admin);

        // The ordering the whole thing rests on. The auto-picker only considers active nodes,
        // and a node that has just had a seat freed is the least loaded one — so if this flag
        // is not set before the moves begin, the fleet hands every user straight back.
        Assert.False(await IsActiveAsync(from));
    }

    [SkippableFact]
    public async Task A_user_with_nowhere_to_go_is_reported_as_stayed_not_moved()
    {
        RequireDb();
        var client = Client();
        var admin = await NewAdminSessionAsync(client);

        var only = await SeedServerAsync();
        var userId = await BindUserAsync(client, only);

        // "Nowhere to go" has to be made true, not assumed. The database is shared across
        // the collection, so servers seeded by other tests are sitting there with free
        // seats and the user lands on one of them.
        var reactivate = await DeactivateOtherServersAsync(only);
        try
        {
            var res = await client.PostJsonAsync($"/admin/servers/{only}/evacuate", new { }, TestData.NewIp(), admin);
            var body = await res.ReadJsonAsync();

        // SelectAsync keeps the existing binding when nothing has room and reports success
        // for it — right for an ordinary move, and a lie here. Counting that as "moved" would
        // report a completed evacuation with everyone still on the blocked node.
            Assert.Equal(0, body.GetProperty("moved").GetInt32());
            Assert.Equal(1, body.GetProperty("stayed").GetInt32());
            Assert.Equal(only, await CurrentServerAsync(userId));
        }
        finally
        {
            // Restore exactly what was switched off, in a finally: a failed assertion must
            // not leave the fleet dark for every test that runs after this one.
            await ReactivateAsync(reactivate);
        }
    }

    [SkippableFact]
    public async Task Running_it_again_finds_nothing_left_to_do()
    {
        RequireDb();
        var client = Client();
        var admin = await NewAdminSessionAsync(client);

        var from = await SeedServerAsync();
        await SeedServerAsync();
        await BindUserAsync(client, from);

        await client.PostJsonAsync($"/admin/servers/{from}/evacuate", new { }, TestData.NewIp(), admin);
        var second = await client.PostJsonAsync($"/admin/servers/{from}/evacuate", new { }, TestData.NewIp(), admin);

        // Re-running is how a partial evacuation is finished, so it has to be safe when
        // there is nothing left rather than an error or a second round of node calls.
        var body = await second.ReadJsonAsync();
        Assert.Equal(0, body.GetProperty("total").GetInt32());
    }

    [SkippableFact]
    public async Task Activate_puts_the_node_back()
    {
        RequireDb();
        var client = Client();
        var admin = await NewAdminSessionAsync(client);

        var id = await SeedServerAsync();
        await client.PostJsonAsync($"/admin/servers/{id}/evacuate", new { }, TestData.NewIp(), admin);
        Assert.False(await IsActiveAsync(id));

        var res = await client.PostJsonAsync($"/admin/servers/{id}/activate", new { }, TestData.NewIp(), admin);

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.True(await IsActiveAsync(id));
    }

    [SkippableFact]
    public async Task Evacuating_a_node_that_does_not_exist_is_a_404()
    {
        RequireDb();
        var client = Client();
        var admin = await NewAdminSessionAsync(client);

        var res = await client.PostJsonAsync("/admin/servers/999999/evacuate", new { }, TestData.NewIp(), admin);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<int> SeedServerAsync(int maxReservations = 5)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<int>("""
            INSERT INTO vpn_servers (name, country, city, host, max_clients, max_reservations, auth_password, is_active)
            VALUES ('E', 'EE', 'EC', @host, @m, @m, 'pw', TRUE)
            RETURNING id
            """, new { host = "evac-" + Guid.NewGuid().ToString("N")[..8] + ".example", m = maxReservations });
    }

    /// <summary>Registers a user and binds them to a node the way a purchase would.</summary>
    private async Task<int> BindUserAsync(HttpClient client, int serverId)
    {
        var (username, _, _) = await RegisterVerifiedUserAsync(client);

        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.ExecuteAsync("""
            UPDATE users SET current_server_id = @s WHERE username = @u;
            UPDATE vpn_servers SET reserved_count = reserved_count + 1 WHERE id = @s;
            """, new { s = serverId, u = username });

        return await conn.ExecuteScalarAsync<int>(
            "SELECT id FROM users WHERE username = @u", new { u = username });
    }

    /// <summary>
    /// Switches off every active server except one and returns what it touched, so the
    /// caller can put it back. Used to make "the fleet is full" true rather than hoping it is.
    /// </summary>
    private async Task<int[]> DeactivateOtherServersAsync(int keep)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);

        var ids = (await conn.QueryAsync<int>(
            "SELECT id FROM vpn_servers WHERE is_active AND id <> @keep", new { keep })).ToArray();

        if (ids.Length > 0)
            await conn.ExecuteAsync(
                "UPDATE vpn_servers SET is_active = FALSE WHERE id = ANY(@ids)", new { ids });

        return ids;
    }

    private async Task ReactivateAsync(int[] ids)
    {
        if (ids.Length == 0) return;

        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.ExecuteAsync(
            "UPDATE vpn_servers SET is_active = TRUE WHERE id = ANY(@ids)", new { ids });
    }

    private async Task<int?> CurrentServerAsync(int userId)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<int?>(
            "SELECT current_server_id FROM users WHERE id = @id", new { id = userId });
    }

    private async Task<bool> IsActiveAsync(int serverId)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT is_active FROM vpn_servers WHERE id = @id", new { id = serverId });
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
