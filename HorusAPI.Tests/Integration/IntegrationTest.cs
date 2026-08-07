using HorusAPI.Tests.Infrastructure;

namespace HorusAPI.Tests.Integration;

/// <summary>
/// Base for every integration test: shares the one <see cref="ApiFixture"/> (DB + API),
/// and <see cref="RequireDb"/> turns "no PostgreSQL" into a Skip rather than a failure.
/// Concrete tests must use <c>[SkippableFact]</c> for that to register as skipped.
/// </summary>
[Collection(ApiCollection.Name)]
public abstract class IntegrationTest(ApiFixture fixture)
{
    protected ApiFixture Fixture { get; } = fixture;

    protected HttpClient Client() => Fixture.NewClient();

    protected void RequireDb() => Skip.IfNot(Fixture.Available, Fixture.SkipReason);

    protected const string Password = "supersecret1";

    /// <summary>Registers a user, confirms the mailed code, and returns a live session token.</summary>
    protected async Task<(string username, string email, string session)> RegisterVerifiedUserAsync(HttpClient client)
    {
        string username = TestData.NewUsername();
        string email    = TestData.NewEmail();
        string ip       = TestData.NewIp();

        var register = await client.PostJsonAsync("/auth/register", new { username, password = Password, email }, ip);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, register.StatusCode);

        string code = Fixture.Email.LastCodeFor(email)
            ?? throw new InvalidOperationException("No verification code was captured.");

        var verify = await client.PostJsonAsync("/auth/verify", new { email, code }, ip);
        Assert.Equal(System.Net.HttpStatusCode.OK, verify.StatusCode);

        string session = await verify.ReadStringPropAsync("session")
            ?? throw new InvalidOperationException("Verify did not return a session.");

        return (username, email, session);
    }

    protected static string TokenFromLink(string link)
    {
        string query = new Uri(link).Query.TrimStart('?');
        string value = query.Split('=', 2)[1];
        return Uri.UnescapeDataString(value);
    }
}
