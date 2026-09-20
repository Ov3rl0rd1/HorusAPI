using System.Net;
using Dapper;
using HorusAPI.Services;
using HorusAPI.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HorusAPI.Tests.Integration;

/// <summary>
/// The half-finished registration: getting back into it, fixing a typo in the address,
/// asking for another code, and eventually being cleaned up.
///
/// The sweeper tests are the ones that matter most. Every foreign key into users is
/// ON DELETE CASCADE, so a sweep that is too eager destroys a paying customer's record and
/// logs nothing — each guard therefore gets a test that proves it holds, not just a comment
/// saying it should.
/// </summary>
public class UnverifiedAccountTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    // ── Getting back in ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Login_to_an_unverified_account_returns_a_ticket_and_a_masked_address()
    {
        RequireDb();
        var client = Client();
        var (username, email) = await RegisterUnverifiedAsync(client);

        var login = await client.PostJsonAsync("/auth/login", new { username, password = Password }, TestData.NewIp());

        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);

        var body = await login.ReadJsonAsync();
        Assert.Equal("email_unverified", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("pendingToken").GetString()));

        // Masked, and masked in a way that still lets someone spot their own typo.
        string masked = body.GetProperty("emailMasked").GetString()!;
        Assert.Contains("*", masked);
        Assert.NotEqual(email, masked);
        Assert.EndsWith(email[email.IndexOf('@')..], masked);
    }

    [SkippableFact]
    public async Task A_pending_ticket_is_not_a_session()
    {
        RequireDb();
        var client = Client();
        string ticket = await PendingTicketAsync(client);

        // The whole point of a separate table: SessionAuthHandler looks in users.sessions[]
        // and never at pending_logins, so this must not open anything a session opens.
        var servers = await client.GetWithAsync("/servers", TestData.NewIp(), ticket);

        Assert.Equal(HttpStatusCode.Unauthorized, servers.StatusCode);
    }

    [SkippableFact]
    public async Task Verify_accepts_a_ticket_instead_of_an_address()
    {
        RequireDb();
        var client = Client();
        var (username, email) = await RegisterUnverifiedAsync(client);
        string code   = Fixture.Email.LastCodeFor(email)!;
        string ticket = await TicketForAsync(client, username);

        // Someone who signed in with their USERNAME was never told the address, and what
        // they were shown is masked — so the ticket has to be enough on its own.
        var verify = await client.PostJsonAsync("/auth/verify",
            new { pending_token = ticket, code }, TestData.NewIp());

        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await verify.ReadStringPropAsync("session")));
    }

    [SkippableFact]
    public async Task A_ticket_stops_working_once_the_account_is_confirmed()
    {
        RequireDb();
        var client = Client();
        var (username, email) = await RegisterUnverifiedAsync(client);
        string ticket = await TicketForAsync(client, username);

        var verify = await client.PostJsonAsync("/auth/verify",
            new { email, code = Fixture.Email.LastCodeFor(email)! }, TestData.NewIp());
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);

        // Confirmed in another tab, so the ticket must be dead even though it has not expired.
        var resend = await client.PostJsonAsync("/auth/resend-code",
            new { pending_token = ticket }, TestData.NewIp());

        Assert.Equal(HttpStatusCode.BadRequest, resend.StatusCode);
        Assert.Equal("invalid_ticket", await resend.ReadStringPropAsync("code"));
    }

    // ── Asking again ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Resend_by_ticket_is_refused_until_the_cooldown_lapses()
    {
        RequireDb();
        var client = Client();
        string ticket = await PendingTicketAsync(client);   // registration just mailed a code

        var resend = await client.PostJsonAsync("/auth/resend-code",
            new { pending_token = ticket }, TestData.NewIp());

        Assert.Equal(HttpStatusCode.TooManyRequests, resend.StatusCode);
        Assert.Equal("resend_too_soon", await resend.ReadStringPropAsync("code"));

        // The client needs a number to count down from, in the header clients already read.
        Assert.NotNull(resend.Headers.RetryAfter);
    }

    [SkippableFact]
    public async Task Resend_by_ticket_works_once_the_cooldown_has_passed()
    {
        RequireDb();
        var client = Client();
        var (username, email) = await RegisterUnverifiedAsync(client);
        string first  = Fixture.Email.LastCodeFor(email)!;
        string ticket = await TicketForAsync(client, username);

        await BackdateLastSendAsync(email, AccountService.ResendCooldown + TimeSpan.FromMinutes(1));

        var resend = await client.PostJsonAsync("/auth/resend-code",
            new { pending_token = ticket }, TestData.NewIp());

        Assert.Equal(HttpStatusCode.Accepted, resend.StatusCode);
        Assert.NotEqual(first, Fixture.Email.LastCodeFor(email));
    }

    [SkippableFact]
    public async Task Resend_by_address_answers_the_same_for_an_unknown_one()
    {
        RequireDb();
        var client = Client();

        // Anti-enumeration: an address nobody registered must be indistinguishable from
        // one that exists and is merely cooling down.
        var unknown = await client.PostJsonAsync("/auth/resend-code",
            new { email = TestData.NewEmail() }, TestData.NewIp());

        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal("unverified", await unknown.ReadStringPropAsync("status"));
    }

    // ── Fixing a typo ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Change_email_moves_the_address_and_mails_a_code_to_it()
    {
        RequireDb();
        var client = Client();
        var (username, oldEmail) = await RegisterUnverifiedAsync(client);
        string ticket   = await TicketForAsync(client, username);
        string newEmail = TestData.NewEmail();

        var change = await client.PostJsonAsync("/auth/change-email",
            new { pending_token = ticket, email = newEmail }, TestData.NewIp());

        Assert.Equal(HttpStatusCode.Accepted, change.StatusCode);
        Assert.Equal(newEmail, await change.ReadStringPropAsync("email"));

        string? code = Fixture.Email.LastCodeFor(newEmail);
        Assert.NotNull(code);

        var verify = await client.PostJsonAsync("/auth/verify",
            new { email = newEmail, code }, TestData.NewIp());
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
    }

    [SkippableFact]
    public async Task Change_email_kills_the_code_that_went_to_the_old_address()
    {
        RequireDb();
        var client = Client();
        var (username, oldEmail) = await RegisterUnverifiedAsync(client);
        string oldCode = Fixture.Email.LastCodeFor(oldEmail)!;
        string ticket  = await TicketForAsync(client, username);

        await client.PostJsonAsync("/auth/change-email",
            new { pending_token = ticket, email = TestData.NewEmail() }, TestData.NewIp());

        // Whoever holds the OLD mailbox must not be able to confirm the new address.
        var verify = await client.PostJsonAsync("/auth/verify",
            new { pending_token = ticket, code = oldCode }, TestData.NewIp());

        Assert.Equal(HttpStatusCode.BadRequest, verify.StatusCode);
    }

    [SkippableFact]
    public async Task Change_email_refuses_an_address_that_belongs_to_someone_else()
    {
        RequireDb();
        var client = Client();
        var (_, taken, _) = await RegisterVerifiedUserAsync(client);
        var (username, _) = await RegisterUnverifiedAsync(client);
        string ticket = await TicketForAsync(client, username);

        var change = await client.PostJsonAsync("/auth/change-email",
            new { pending_token = ticket, email = taken }, TestData.NewIp());

        Assert.Equal(HttpStatusCode.Conflict, change.StatusCode);
        Assert.Equal("email_taken", await change.ReadStringPropAsync("code"));
    }

    [SkippableFact]
    public async Task Change_email_refuses_the_address_it_already_has()
    {
        RequireDb();
        var client = Client();
        var (username, email) = await RegisterUnverifiedAsync(client);
        string ticket = await TicketForAsync(client, username);

        // Otherwise "changing" the address to itself would re-mail a code and reset the
        // cooldown, which is the cooldown gone.
        var change = await client.PostJsonAsync("/auth/change-email",
            new { pending_token = ticket, email }, TestData.NewIp());

        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        Assert.Equal("email_unchanged", await change.ReadStringPropAsync("code"));
    }

    // ── Sweeping ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Sweep_deletes_an_abandoned_unverified_account()
    {
        RequireDb();
        var client = Client();
        var (username, email) = await RegisterUnverifiedAsync(client);
        await BackdateCreationAsync(email, TimeSpan.FromDays(30));

        int removed = await SweepAsync(TimeSpan.FromDays(7));

        Assert.True(removed >= 1);
        Assert.False(await AccountExistsAsync(email));

        // And the username is free again, which is the whole point: the person who
        // mistyped their address can register properly.
        var again = await client.PostJsonAsync("/auth/register",
            new { username, password = Password, email = TestData.NewEmail() }, TestData.NewIp());
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
    }

    [SkippableFact]
    public async Task Sweep_leaves_a_recent_unverified_account_alone()
    {
        RequireDb();
        var client = Client();
        var (_, email) = await RegisterUnverifiedAsync(client);

        await SweepAsync(TimeSpan.FromDays(7));

        Assert.True(await AccountExistsAsync(email));
    }

    [SkippableFact]
    public async Task Sweep_leaves_a_verified_account_alone()
    {
        RequireDb();
        var client = Client();
        var (_, email, _) = await RegisterVerifiedUserAsync(client);
        await BackdateCreationAsync(email, TimeSpan.FromDays(400));

        await SweepAsync(TimeSpan.FromDays(7));

        Assert.True(await AccountExistsAsync(email));
    }

    [SkippableFact]
    public async Task Sweep_spares_an_unverified_account_that_still_holds_a_session()
    {
        RequireDb();
        var client = Client();
        var (_, email) = await RegisterUnverifiedAsync(client);
        await BackdateCreationAsync(email, TimeSpan.FromDays(30));

        // Cannot happen through the API today — login refuses an unverified account a
        // session. The guard is for the day something else forgets that rule.
        await ExecuteAsync("UPDATE users SET sessions = ARRAY['leftover'] WHERE lower(email) = lower(@Email)",
            new { Email = email });

        await SweepAsync(TimeSpan.FromDays(7));

        Assert.True(await AccountExistsAsync(email));
    }

    [SkippableFact]
    public async Task Sweep_spares_an_unverified_account_that_still_has_access()
    {
        RequireDb();
        var client = Client();
        var (_, email) = await RegisterUnverifiedAsync(client);
        await BackdateCreationAsync(email, TimeSpan.FromDays(30));

        await ExecuteAsync(
            "UPDATE users SET expires_at = NOW() + INTERVAL '30 days' WHERE lower(email) = lower(@Email)",
            new { Email = email });

        await SweepAsync(TimeSpan.FromDays(7));

        Assert.True(await AccountExistsAsync(email));
    }

    [SkippableFact]
    public async Task Sweep_spares_an_unverified_account_that_has_a_subscription()
    {
        RequireDb();
        var client = Client();
        var (_, email) = await RegisterUnverifiedAsync(client);
        await BackdateCreationAsync(email, TimeSpan.FromDays(30));

        // The expensive mistake this guard exists to prevent: subscriptions cascade, so
        // deleting the user here would erase the record of a payment that really happened.
        await ExecuteAsync("""
            INSERT INTO subscriptions (user_id, kind, status)
            SELECT id, 'one_time', 'active' FROM users WHERE lower(email) = lower(@Email)
            """, new { Email = email });

        await SweepAsync(TimeSpan.FromDays(7));

        Assert.True(await AccountExistsAsync(email));
    }

    // ── What the confirmation screen needs ───────────────────────────────────

    [SkippableFact]
    public async Task Register_hands_back_a_pending_ticket()
    {
        RequireDb();
        var client = Client();

        var register = await client.PostJsonAsync("/auth/register",
            new { username = TestData.NewUsername(), password = Password, email = TestData.NewEmail() },
            TestData.NewIp());

        // Without this the "wrong address?" link cannot exist at the one moment a typo is
        // most likely to be noticed: a second after it was typed.
        var body = await register.ReadJsonAsync();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("pendingToken").GetString()));
    }

    [SkippableFact]
    public async Task Resend_by_address_never_hands_out_a_ticket()
    {
        RequireDb();
        var client = Client();
        var (_, email) = await RegisterUnverifiedAsync(client);

        // The invariant that keeps a ticket meaningful. /auth/resend-code is anonymous and
        // answers for ANY address, so a ticket here would let anyone claim any account just
        // by naming its e-mail.
        var resend = await client.PostJsonAsync("/auth/resend-code", new { email }, TestData.NewIp());
        var body = await resend.ReadJsonAsync();

        Assert.True(body.TryGetProperty("pendingToken", out var token));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, token.ValueKind);
    }

    [SkippableFact]
    public async Task A_wrong_code_tells_a_ticket_holder_how_many_guesses_remain()
    {
        RequireDb();
        var client = Client();
        var (username, _) = await RegisterUnverifiedAsync(client);
        string ticket = await TicketForAsync(client, username);

        var verify = await client.PostJsonAsync("/auth/verify",
            new { pending_token = ticket, code = "000000" }, TestData.NewIp());

        Assert.Equal(HttpStatusCode.BadRequest, verify.StatusCode);
        Assert.Equal(4, (await verify.ReadJsonAsync()).GetProperty("attemptsLeft").GetInt32());
    }

    [SkippableFact]
    public async Task A_wrong_code_by_address_keeps_the_counter_to_itself()
    {
        RequireDb();
        var client = Client();
        var (_, email) = await RegisterUnverifiedAsync(client);

        // An address nobody registered would report 0 while a real unconfirmed one reports 4,
        // so an anonymous caller must not see the number at all — otherwise it answers
        // "is there a pending registration here".
        var real = await client.PostJsonAsync("/auth/verify",
            new { email, code = "000000" }, TestData.NewIp());
        var unknown = await client.PostJsonAsync("/auth/verify",
            new { email = TestData.NewEmail(), code = "000000" }, TestData.NewIp());

        Assert.Equal(HttpStatusCode.BadRequest, real.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.False((await real.ReadJsonAsync()).TryGetProperty("attemptsLeft", out _));
        Assert.Equal(
            await unknown.ReadStringPropAsync("code"),
            await real.ReadStringPropAsync("code"));
    }

    [SkippableFact]
    public async Task The_fifth_wrong_code_burns_the_code()
    {
        RequireDb();
        var client = Client();
        var (username, _) = await RegisterUnverifiedAsync(client);
        string ticket = await TicketForAsync(client, username);
        string ip = TestData.NewIp();

        for (var i = 0; i < 4; i++)
            await client.PostJsonAsync("/auth/verify", new { pending_token = ticket, code = "000000" }, ip);

        // Fifth guess: the code dies rather than the attempt simply failing, which is what
        // puts the screen into its "ask for a new one" state.
        var fifth = await client.PostJsonAsync("/auth/verify",
            new { pending_token = ticket, code = "000000" }, ip);

        Assert.Equal(HttpStatusCode.TooManyRequests, fifth.StatusCode);
        Assert.Equal("too_many_attempts", await fifth.ReadStringPropAsync("code"));
    }

    [SkippableFact]
    public async Task Login_reports_how_long_the_pending_code_is_still_good_for()
    {
        RequireDb();
        var client = Client();
        var (username, email) = await RegisterUnverifiedAsync(client);

        var login = await client.PostJsonAsync("/auth/login", new { username, password = Password }, TestData.NewIp());
        int life = (await login.ReadJsonAsync()).GetProperty("codeExpiresInSeconds").GetInt32();

        // The code was mailed seconds ago, so nearly its whole life is left. Without this the
        // confirmation screen cannot show a countdown at all when the user signed in rather
        // than arriving straight from registration.
        Assert.InRange(life, 1, (int)AccountService.CodeLifetime.TotalSeconds);
    }

    [SkippableFact]
    public async Task An_expired_code_reports_no_remaining_life()
    {
        RequireDb();
        var client = Client();
        var (username, email) = await RegisterUnverifiedAsync(client);

        await ExecuteAsync("""
            UPDATE email_verifications SET expires_at = NOW() - INTERVAL '1 minute'
            WHERE user_id = (SELECT id FROM users WHERE lower(email) = lower(@Email))
            """, new { Email = email });

        var login = await client.PostJsonAsync("/auth/login", new { username, password = Password }, TestData.NewIp());

        // Zero rather than a negative number, so the screen shows nothing instead of
        // counting down from a code that is already gone.
        Assert.Equal(0, (await login.ReadJsonAsync()).GetProperty("codeExpiresInSeconds").GetInt32());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<(string username, string email)> RegisterUnverifiedAsync(HttpClient client)
    {
        string username = TestData.NewUsername(), email = TestData.NewEmail();

        var register = await client.PostJsonAsync("/auth/register",
            new { username, password = Password, email }, TestData.NewIp());
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);

        return (username, email);
    }

    private async Task<string> TicketForAsync(HttpClient client, string username)
    {
        var login = await client.PostJsonAsync("/auth/login",
            new { username, password = Password }, TestData.NewIp());
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);

        return (await login.ReadJsonAsync()).GetProperty("pendingToken").GetString()!;
    }

    private async Task<string> PendingTicketAsync(HttpClient client)
    {
        var (username, _) = await RegisterUnverifiedAsync(client);
        return await TicketForAsync(client, username);
    }

    private async Task<int> SweepAsync(TimeSpan ttl)
    {
        using var scope = Fixture.Factory!.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        return await accounts.DeleteStaleUnverifiedAsync(ttl);
    }

    private Task BackdateCreationAsync(string email, TimeSpan by) => ExecuteAsync(
        "UPDATE users SET created_at = NOW() - @Age::interval WHERE lower(email) = lower(@Email)",
        new { Email = email, Age = $"{(int)by.TotalMinutes} minutes" });

    private Task BackdateLastSendAsync(string email, TimeSpan by) => ExecuteAsync("""
        UPDATE email_verifications SET sent_at = NOW() - @Age::interval
        WHERE user_id = (SELECT id FROM users WHERE lower(email) = lower(@Email))
        """, new { Email = email, Age = $"{(int)by.TotalMinutes} minutes" });

    private async Task<bool> AccountExistsAsync(string email)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM users WHERE lower(email) = lower(@Email))", new { Email = email });
    }

    private async Task ExecuteAsync(string sql, object parameters)
    {
        await using var conn = new NpgsqlConnection(Fixture.ConnectionString);
        await conn.ExecuteAsync(sql, parameters);
    }
}
