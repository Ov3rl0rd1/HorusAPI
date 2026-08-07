using System.Net;
using Dapper;
using HorusAPI.Tests.Infrastructure;
using Npgsql;

namespace HorusAPI.Tests.Integration;

public class AuthorizationTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    [SkippableFact]
    public async Task Servers_require_a_session()
    {
        RequireDb();
        var res = await Client().GetWithAsync("/servers/best", TestData.NewIp(), session: null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [SkippableFact]
    public async Task Servers_are_reachable_with_a_valid_session()
    {
        RequireDb();
        var client = Client();
        var (_, _, session) = await RegisterVerifiedUserAsync(client);

        var res = await client.GetWithAsync("/servers/best", TestData.NewIp(), session);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_routes_require_a_session()
    {
        RequireDb();
        var res = await Client().GetWithAsync("/admin/servers", TestData.NewIp(), session: null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_routes_forbid_a_non_admin()
    {
        RequireDb();
        var client = Client();
        var (_, _, session) = await RegisterVerifiedUserAsync(client);

        var res = await client.GetWithAsync("/admin/servers", TestData.NewIp(), session);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_routes_allow_an_admin()
    {
        RequireDb();
        var client = Client();
        var (username, _, _) = await RegisterVerifiedUserAsync(client);

        await using (var conn = new NpgsqlConnection(Fixture.ConnectionString))
            await conn.ExecuteAsync("UPDATE users SET is_admin = TRUE WHERE username = @username", new { username });

        // Fresh login so the new session carries the Admin role.
        var login = await client.PostJsonAsync("/auth/login", new { username, password = Password }, TestData.NewIp());
        string adminSession = (await login.ReadStringPropAsync("session"))!;

        var res = await client.GetWithAsync("/admin/servers", TestData.NewIp(), adminSession);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
