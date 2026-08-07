using System.Net;
using HorusAPI.Tests.Infrastructure;

namespace HorusAPI.Tests.Integration;

public class AuthFlowTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    [SkippableFact]
    public async Task Register_creates_unverified_account_and_mails_a_code()
    {
        RequireDb();
        var client = Client();
        string username = TestData.NewUsername(), email = TestData.NewEmail(), ip = TestData.NewIp();

        var res = await client.PostJsonAsync("/auth/register", new { username, password = Password, email }, ip);

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal("unverified", await res.ReadStringPropAsync("status"));

        string? code = Fixture.Email.LastCodeFor(email);
        Assert.NotNull(code);
        Assert.Matches(@"^\d{6}$", code!);
    }

    [SkippableFact]
    public async Task Login_before_verification_is_forbidden()
    {
        RequireDb();
        var client = Client();
        string username = TestData.NewUsername(), email = TestData.NewEmail(), ip = TestData.NewIp();

        await client.PostJsonAsync("/auth/register", new { username, password = Password, email }, ip);
        var login = await client.PostJsonAsync("/auth/login", new { username, password = Password }, TestData.NewIp());

        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("email_unverified", await login.ReadStringPropAsync("code"));
    }

    [SkippableFact]
    public async Task Verify_rejects_a_wrong_code()
    {
        RequireDb();
        var client = Client();
        string username = TestData.NewUsername(), email = TestData.NewEmail(), ip = TestData.NewIp();

        await client.PostJsonAsync("/auth/register", new { username, password = Password, email }, ip);
        var verify = await client.PostJsonAsync("/auth/verify", new { email, code = "000000" }, TestData.NewIp());

        Assert.Equal(HttpStatusCode.BadRequest, verify.StatusCode);
        Assert.Equal("invalid_code", await verify.ReadStringPropAsync("code"));
    }

    [SkippableFact]
    public async Task Verify_with_correct_code_yields_a_session_and_enables_login()
    {
        RequireDb();
        var client = Client();
        var (username, _, session) = await RegisterVerifiedUserAsync(client);

        Assert.False(string.IsNullOrWhiteSpace(session));

        var login = await client.PostJsonAsync("/auth/login", new { username, password = Password }, TestData.NewIp());
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await login.ReadStringPropAsync("session")));
    }

    [SkippableFact]
    public async Task Duplicate_username_and_email_are_reported_distinctly()
    {
        RequireDb();
        var client = Client();
        string username = TestData.NewUsername(), email = TestData.NewEmail();

        var first = await client.PostJsonAsync("/auth/register", new { username, password = Password, email }, TestData.NewIp());
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var sameName = await client.PostJsonAsync("/auth/register",
            new { username, password = Password, email = TestData.NewEmail() }, TestData.NewIp());
        Assert.Equal(HttpStatusCode.Conflict, sameName.StatusCode);
        Assert.Equal("username_taken", await sameName.ReadStringPropAsync("code"));

        var sameEmail = await client.PostJsonAsync("/auth/register",
            new { username = TestData.NewUsername(), password = Password, email = email.ToUpperInvariant() }, TestData.NewIp());
        Assert.Equal(HttpStatusCode.Conflict, sameEmail.StatusCode);
        Assert.Equal("email_taken", await sameEmail.ReadStringPropAsync("code"));
    }

    [SkippableFact]
    public async Task Password_reset_rotates_the_password_and_revokes_sessions()
    {
        RequireDb();
        var client = Client();
        var (username, email, oldSession) = await RegisterVerifiedUserAsync(client);

        // Request the reset link.
        var request = await client.PostJsonAsync("/auth/reset-request", new { email }, TestData.NewIp());
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);

        string token = TokenFromLink(Fixture.Email.LastLinkFor(email)!);

        var check = await client.GetWithAsync($"/auth/reset-check?token={Uri.EscapeDataString(token)}", TestData.NewIp(), session: null);
        Assert.Equal("valid", await check.ReadStringPropAsync("status"));

        const string newPassword = "brand-new-pass-9";
        var confirm = await client.PostJsonAsync("/auth/reset-confirm", new { token, password = newPassword }, TestData.NewIp());
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        // Old session was revoked by the reset.
        var oldSessionCall = await client.GetWithAsync("/whoami", TestData.NewIp(), oldSession);
        Assert.Equal(HttpStatusCode.Unauthorized, oldSessionCall.StatusCode);

        // Old password no longer works; the new one does.
        var oldLogin = await client.PostJsonAsync("/auth/login", new { username, password = Password }, TestData.NewIp());
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await client.PostJsonAsync("/auth/login", new { username, password = newPassword }, TestData.NewIp());
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);

        // The token is single-use.
        var reuse = await client.PostJsonAsync("/auth/reset-confirm", new { token, password = "another-pass-9" }, TestData.NewIp());
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
        Assert.Equal("invalid_token", await reuse.ReadStringPropAsync("code"));
    }

    [SkippableFact]
    public async Task Reset_request_for_unknown_address_is_indistinguishable()
    {
        RequireDb();
        var client = Client();
        string unknown = TestData.NewEmail();

        var res = await client.PostJsonAsync("/auth/reset-request", new { email = unknown }, TestData.NewIp());

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal("sent", await res.ReadStringPropAsync("status"));
        Assert.False(Fixture.Email.WasAnythingSentTo(unknown));   // nothing actually mailed
    }
}
