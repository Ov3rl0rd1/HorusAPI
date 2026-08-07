using System.Net;
using HorusAPI.Tests.Infrastructure;

namespace HorusAPI.Tests.Integration;

public class WhoAmiTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    [SkippableFact]
    public async Task Whoami_requires_a_session()
    {
        RequireDb();
        var res = await Client().GetWithAsync("/whoami", TestData.NewIp(), session: null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [SkippableFact]
    public async Task Whoami_reflects_the_forwarded_client_ip_and_account()
    {
        RequireDb();
        var client = Client();
        var (username, email, session) = await RegisterVerifiedUserAsync(client);

        string ip = TestData.NewIp();
        var res = await client.GetWithAsync("/whoami", ip, session);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.ReadJsonAsync();
        Assert.Equal(ip, body.GetProperty("ip").GetString());          // egress IP == the forwarded client IP
        Assert.Equal("IPv4", body.GetProperty("ipVersion").GetString());
        Assert.Equal(username, body.GetProperty("username").GetString());
        Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.True(body.GetProperty("emailVerified").GetBoolean());
    }
}
