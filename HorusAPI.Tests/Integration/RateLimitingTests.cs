using System.Net;
using HorusAPI.Tests.Infrastructure;

namespace HorusAPI.Tests.Integration;

public class RateLimitingTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    [SkippableFact]
    public async Task Login_is_capped_per_ip()
    {
        RequireDb();
        var client = Client();
        string ip = TestData.NewIp();   // one IP → one login partition (15 / 5 min)

        var codes = new List<HttpStatusCode>();
        for (int i = 0; i < 16; i++)
        {
            var res = await client.PostJsonAsync("/auth/login",
                new { username = "nobody", password = "wrong-password" }, ip);
            codes.Add(res.StatusCode);
        }

        Assert.All(codes.Take(15), c => Assert.Equal(HttpStatusCode.Unauthorized, c));
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[15]);
    }

    [SkippableFact]
    public async Task Mail_routes_are_capped_per_ip_per_minute()
    {
        RequireDb();
        var client = Client();
        string ip = TestData.NewIp();   // shared IP → the 3/min mail bucket applies

        var codes = new List<HttpStatusCode>();
        for (int i = 0; i < 4; i++)
        {
            var res = await client.PostJsonAsync("/auth/register",
                new { username = TestData.NewUsername(), password = Password, email = TestData.NewEmail() }, ip);
            codes.Add(res.StatusCode);
        }

        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.Accepted));
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[3]);
    }

    [SkippableFact]
    public async Task Mail_is_capped_per_account_even_across_ips()
    {
        RequireDb();
        var client = Client();
        string email = TestData.NewEmail();   // one address, four different IPs

        var codes = new List<HttpStatusCode>();
        for (int i = 0; i < 4; i++)
        {
            var res = await client.PostJsonAsync("/auth/reset-request", new { email }, TestData.NewIp());
            codes.Add(res.StatusCode);
        }

        // 3 per hour per address, regardless of source IP → the 4th is throttled.
        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.Accepted));
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[3]);
    }
}
