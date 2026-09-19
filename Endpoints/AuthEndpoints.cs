using Microsoft.AspNetCore.Mvc;
using HorusAPI.Models;
using HorusAPI.Services;
using System.Net.Mail;
using System.Text.RegularExpressions;
using HorusAPI.Services.Auth_Handler;

namespace HorusAPI.Endpoints;

public static class AuthEndpoints
{
    private static readonly Regex UsernameRegex = new(@"^[a-zA-Z0-9_]{3,32}$", RegexOptions.Compiled);

    private const int MinPasswordLength = 8;
    private const int MaxPasswordLength = 128;

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth");

        // ── Login ────────────────────────────────────────────────────────────
        group.MapPost("/login", async (
            [FromBody] LoginRequest req,
            IUserService     userSvc,
            IAccountService  accounts,
            ILogger<Program> log) =>
        {
            // The `username` field accepts either a username or an e-mail.
            if (string.IsNullOrWhiteSpace(req.username) ||
                string.IsNullOrWhiteSpace(req.password))
                return Results.BadRequest(new ApiError("Username or e-mail and password are required."));

            User? user;
            string? session;

            try
            {
                user = await userSvc.AuthenticateAsync(req.username.Trim(), req.password);
                if (user is null) return Results.Unauthorized();

                // An unverified account cannot have a session, but the person holding the
                // right password is still its owner — so this is a door into the confirmation
                // screen rather than a dead end. They get a ticket, not a session: it opens
                // resend / change-address / confirm and nothing else.
                //
                // The address comes back MASKED. They already proved they own the account, so
                // showing it in full would not be a leak, but the confirmation screen is the
                // one place a mistyped address has to be recognisable at a glance, and
                // "a***n@gmail.com" does that without putting a full address on screen.
                if (!user.email_verified)
                {
                    string ticket = await accounts.IssuePendingTicketAsync(user.id);
                    TimeSpan wait = await accounts.ResendCooldownRemainingAsync(user.id);

                    log.LogInformation("Unverified account {Username} signed in to finish confirmation", user.username);

                    return Results.Json(new PendingVerificationResponse(
                        Message:                  "E-mail is not confirmed.",
                        Code:                     "email_unverified",
                        emailMasked:              MaskEmail(user.email),
                        pendingToken:             ticket,
                        pendingExpiresInSeconds:  (int)AccountService.PendingTicketLifetime.TotalSeconds,
                        resendAvailableInSeconds: (int)Math.Ceiling(wait.TotalSeconds)),
                        statusCode: 403);
                }

                session = await userSvc.CreateSession(user.id);
                if (session is null) return Results.Problem("Session creation failed.", statusCode: 500);
            }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            log.LogInformation("User {Username} authenticated", user.username);
            return Results.Ok(new LoginResponse(session, user.expires_at));
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Login)
        .Produces<LoginResponse>(200)
        .Produces<ApiError>(400)
        .Produces<ApiError>(403)
        .Produces(401);


        // ── Register → mails a 6-digit code, account starts unverified ───────
        group.MapPost("/register", async (
            [FromBody] RegisterRequest req,
            IUserService         userSvc,
            IAccountService      accounts,
            IEmailSender         mail,
            IAccountRateLimiter  quota,
            ILogger<Program>     log) =>
        {
            string username = req.username ?? string.Empty;
            if (!UsernameRegex.IsMatch(username))
                return Results.BadRequest(new ApiError("Username must be 3–32 alphanumeric characters or underscores."));

            if (!IsAcceptablePassword(req.password))
                return Results.BadRequest(new ApiError($"Password must be {MinPasswordLength}–{MaxPasswordLength} characters."));

            if (!IsValidEmail(req.email))
                return Results.BadRequest(new ApiError("A valid e-mail address is required."));

            string email = req.email.Trim();

            CreateUserResult created;
            try { created = await userSvc.CreateUserAsync(username, req.password, email); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            switch (created.status)
            {
                case CreateUserStatus.UsernameTaken:
                    return Results.Conflict(new ApiError("Username already taken.", "username_taken"));
                case CreateUserStatus.EmailTaken:
                    return Results.Conflict(new ApiError("E-mail already registered.", "email_taken"));
            }

            // Per-account layer: at most a few messages per hour to this address,
            // however many IPs ask. Charged only now that a mail is actually going
            // out, so a run of username-taken retries doesn't burn the address's
            // budget. The per-IP + global mail limiters already covered those.
            if (!await quota.TryAcquireAsync(email))
                return TooManyEmails();

            try
            {
                string code = await accounts.IssueVerificationCodeAsync(created.userId);
                await mail.SendVerificationCodeAsync(email, username, code, AccountService.CodeLifetime);
            }
            catch (Exception ex)
            {
                // The account exists; the client can ask for a fresh code.
                log.LogError(ex, "Could not issue verification code for {Username}", username);
            }

            log.LogInformation("New user registered (unverified): {Username}", username);

            return Results.Json(
                new RegisterResponse("unverified", email, (int)AccountService.CodeLifetime.TotalSeconds),
                statusCode: 202);
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Email)
        .Produces<RegisterResponse>(202)
        .Produces<ApiError>(400)
        .Produces<ApiError>(409)
        .Produces<ApiError>(429)
        .WithSummary("Register an account and mail a 6-digit confirmation code");


        // ── Confirm the code → account becomes usable, caller gets a session ──
        group.MapPost("/verify", async (
            [FromBody] VerifyRequest req,
            IAccountService  accounts,
            IUserService     userSvc,
            ILogger<Program> log) =>
        {
            if (string.IsNullOrWhiteSpace(req.code))
                return Results.BadRequest(new ApiError("A confirmation code is required."));

            VerifyStatus status;
            User? user;
            try
            {
                // A ticket identifies the account on its own, which is the only way someone
                // who signed in with their USERNAME can get here: the client was never told
                // the address, and what it was told is masked.
                string? email = req.email?.Trim();
                if (!string.IsNullOrWhiteSpace(req.pending_token))
                {
                    User? pending = await accounts.FindByPendingTicketAsync(req.pending_token!);
                    if (pending is null)
                        return Results.BadRequest(new ApiError("This session has expired. Sign in again.", "invalid_ticket"));
                    email = pending.email;
                }
                else if (!IsValidEmail(email))
                {
                    return Results.BadRequest(new ApiError("E-mail and code are required."));
                }

                (status, user) = await accounts.VerifyEmailAsync(email!, req.code.Trim());
            }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            switch (status)
            {
                case VerifyStatus.AlreadyVerified:
                    return Results.Conflict(new ApiError("E-mail already confirmed.", "already_verified"));

                case VerifyStatus.TooManyAttempts:
                    return Results.Json(
                        new ApiError("Too many wrong codes. Request a new one.", "too_many_attempts"),
                        statusCode: 429);

                case VerifyStatus.Expired:
                    return Results.BadRequest(new ApiError("The code has expired. Request a new one.", "code_expired"));

                case VerifyStatus.NotFound:
                case VerifyStatus.Invalid:
                    // Same answer either way: never reveal whether the address is registered.
                    return Results.BadRequest(new ApiError("Invalid code.", "invalid_code"));
            }

            string? session;
            try
            {
                session = await userSvc.CreateSession(user!.id);
                if (session is null) return Results.Problem("Session creation failed.", statusCode: 500);
            }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            log.LogInformation("User {Username} confirmed their e-mail", user!.username);
            return Results.Ok(new LoginResponse(session, user.expires_at));
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Verify)
        .Produces<LoginResponse>(200)
        .Produces<ApiError>(400)
        .Produces<ApiError>(409)
        .Produces<ApiError>(429)
        .WithSummary("Confirm an e-mail with the 6-digit code and receive a session");


        // ── Resend the code ────────────────────────────────────────
        group.MapPost("/resend-code", async (
            [FromBody] ResendCodeRequest req,
            IAccountService     accounts,
            IEmailSender        mail,
            IAccountRateLimiter quota,
            ILogger<Program>    log) =>
        {
            int cooldown = (int)AccountService.ResendCooldown.TotalSeconds;

            // ── Ticket path: the caller proved they own this account ─────
            // So the cooldown is checked BEFORE the hourly quota, and the true number of
            // seconds comes back. Nothing here can leak anything, because nothing here is
            // reachable without the account's password.
            if (!string.IsNullOrWhiteSpace(req.pending_token))
            {
                User? pending;
                try { pending = await accounts.FindByPendingTicketAsync(req.pending_token!); }
                catch { return Results.Problem("Database error.", statusCode: 503); }

                if (pending is null)
                    return Results.BadRequest(new ApiError("This session has expired. Sign in again.", "invalid_ticket"));

                try
                {
                    TimeSpan wait = await accounts.ResendCooldownRemainingAsync(pending.id);
                    if (wait > TimeSpan.Zero)
                        return TooSoon((int)Math.Ceiling(wait.TotalSeconds));

                    if (!await quota.TryAcquireAsync(pending.email))
                        return TooManyEmails();

                    string code = await accounts.IssueVerificationCodeAsync(pending.id);
                    await mail.SendVerificationCodeAsync(pending.email, pending.username, code, AccountService.CodeLifetime);
                }
                catch { return Results.Problem("Database error.", statusCode: 503); }

                return Results.Json(
                    new RegisterResponse("unverified", MaskEmail(pending.email),
                        (int)AccountService.CodeLifetime.TotalSeconds, cooldown),
                    statusCode: 202);
            }

            // ── Address path: anonymous, so it must not answer questions ──
            if (!IsValidEmail(req.email))
                return Results.BadRequest(new ApiError("A valid e-mail address is required."));

            string email = req.email!.Trim();

            // Charged for EVERY address, real or not, and before the cooldown is even
            // looked at. That ordering IS the anti-enumeration property: if the quota were
            // charged only when a mail actually goes out, an unknown address would
            // eventually answer 429 while a real one in cooldown never would, and that
            // difference is an account oracle. The cost is that an impatient user can spend
            // their hourly permits on nothing, which is exactly why the response now carries
            // a countdown for the button to obey.
            if (!await quota.TryAcquireAsync(email))
                return TooManyEmails();

            try
            {
                User? user = await accounts.FindByEmailAsync(email);

                // Unknown, already-confirmed and still-cooling-down addresses all take this
                // same path and produce the same answer.
                if (user is not null && !user.email_verified &&
                    await accounts.ResendCooldownRemainingAsync(user.id) <= TimeSpan.Zero)
                {
                    string code = await accounts.IssueVerificationCodeAsync(user.id);
                    await mail.SendVerificationCodeAsync(email, user.username, code, AccountService.CodeLifetime);
                }
                else
                {
                    log.LogInformation("Resend requested for an unknown, confirmed or cooling-down address");
                }
            }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            // Always the full cooldown, never the true remaining time: an address that was
            // mailed fifteen seconds ago must not be distinguishable from one that does not
            // exist. The ticket path above returns the real figure, because there it is the
            // caller's own account.
            return Results.Json(
                new RegisterResponse("unverified", email, (int)AccountService.CodeLifetime.TotalSeconds, cooldown),
                statusCode: 202);
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Email)
        .Produces<RegisterResponse>(202)
        .Produces<ApiError>(400)
        .Produces<ApiError>(429)
        .WithSummary("Mail a fresh confirmation code (by address, or by pending ticket)");


        // ── Correct a mistyped address, before confirmation ──────────
        // Needs a ticket, which means the account's password. Without this the only way out
        // of a typo was to abandon the account — which then sat on its username and its
        // address indefinitely, so the same person could not even register again correctly.
        group.MapPost("/change-email", async (
            [FromBody] ChangeEmailRequest req,
            IAccountService     accounts,
            IEmailSender        mail,
            IAccountRateLimiter quota,
            ILogger<Program>    log) =>
        {
            if (string.IsNullOrWhiteSpace(req.pending_token))
                return Results.BadRequest(new ApiError("This session has expired. Sign in again.", "invalid_ticket"));

            if (!IsValidEmail(req.email))
                return Results.BadRequest(new ApiError("A valid e-mail address is required."));

            string email = req.email.Trim();

            User? pending;
            try { pending = await accounts.FindByPendingTicketAsync(req.pending_token); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            if (pending is null)
                return Results.BadRequest(new ApiError("This session has expired. Sign in again.", "invalid_ticket"));

            // Nothing to do — and refusing it closes a small hole, because "changing" the
            // address to itself would otherwise re-mail a code and reset the cooldown.
            if (string.Equals(email, pending.email, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new ApiError("That is already the address on this account.", "email_unchanged"));

            // Charged against the NEW address: it is the one about to receive mail, and the
            // one a stranger could be pointed at.
            if (!await quota.TryAcquireAsync(email))
                return TooManyEmails();

            ChangeEmailStatus status;
            try { status = await accounts.ChangeEmailAsync(pending.id, email); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            switch (status)
            {
                case ChangeEmailStatus.EmailTaken:
                    return Results.Conflict(new ApiError("E-mail already registered.", "email_taken"));
                case ChangeEmailStatus.AlreadyVerified:
                    return Results.Conflict(new ApiError("This account is already confirmed.", "already_verified"));
                case ChangeEmailStatus.NotFound:
                    return Results.BadRequest(new ApiError("This session has expired. Sign in again.", "invalid_ticket"));
            }

            try
            {
                string code = await accounts.IssueVerificationCodeAsync(pending.id);
                await mail.SendVerificationCodeAsync(email, pending.username, code, AccountService.CodeLifetime);
            }
            catch (Exception ex)
            {
                // The address is already changed; the client can ask for a fresh code.
                log.LogError(ex, "Could not mail a code to the corrected address for user {UserId}", pending.id);
            }

            log.LogInformation("User {UserId} corrected their address before confirming", pending.id);

            // The ticket deliberately stays valid: the same screen keeps working, and nobody
            // has to sign in again just to fix a typo.
            return Results.Json(
                new RegisterResponse("unverified", email, (int)AccountService.CodeLifetime.TotalSeconds,
                    (int)AccountService.ResendCooldown.TotalSeconds),
                statusCode: 202);
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Email)
        .Produces<RegisterResponse>(202)
        .Produces<ApiError>(400)
        .Produces<ApiError>(409)
        .Produces<ApiError>(429)
        .WithSummary("Correct the address on an unconfirmed account and mail a new code");


        // ── Password reset: request the link ─────────────────────────────────
        group.MapPost("/reset-request", async (
            [FromBody] ResetRequest req,
            HttpContext         ctx,
            IAccountService     accounts,
            IEmailSender        mail,
            IAccountRateLimiter quota,
            IConfiguration      cfg,
            ILogger<Program>    log) =>
        {
            if (!IsValidEmail(req.email))
                return Results.BadRequest(new ApiError("A valid e-mail address is required."));

            string email = req.email.Trim();

            if (!await quota.TryAcquireAsync(email))
                return TooManyEmails();

            try
            {
                ResetTicket? ticket = await accounts.IssueResetTokenAsync(email);

                if (ticket is null)
                {
                    log.LogInformation("Password reset requested for an unknown address");
                }
                else
                {
                    string link = $"{PublicBaseUrl(ctx, cfg)}/reset?token={Uri.EscapeDataString(ticket.token)}";
                    await mail.SendPasswordResetAsync(email, ticket.username, link, AccountService.ResetLifetime);
                }
            }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            // Always the same answer — this must not confirm who has an account.
            return Results.Json(new StatusResponse("sent"), statusCode: 202);
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Email)
        .Produces<StatusResponse>(202)
        .Produces<ApiError>(400)
        .Produces<ApiError>(429)
        .WithSummary("Mail a password-reset link (always answers 202)");


        // ── Password reset: is this link still good? (used by /reset page) ───
        group.MapGet("/reset-check", async (
            [FromQuery] string? token,
            IAccountService accounts) =>
        {
            bool valid;
            try { valid = await accounts.IsResetTokenValidAsync(token ?? string.Empty); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            return Results.Ok(new StatusResponse(valid ? "valid" : "invalid"));
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Verify)
        .Produces<StatusResponse>(200)
        .WithSummary("Check whether a reset link is still usable");


        // ── Password reset: set the new password ─────────────────────────────
        group.MapPost("/reset-confirm", async (
            [FromBody] ResetConfirmRequest req,
            IAccountService  accounts,
            ILogger<Program> log) =>
        {
            if (string.IsNullOrWhiteSpace(req.token))
                return Results.BadRequest(new ApiError("Reset token is required.", "invalid_token"));

            if (!IsAcceptablePassword(req.password))
                return Results.BadRequest(new ApiError($"Password must be {MinPasswordLength}–{MaxPasswordLength} characters."));

            ResetStatus status;
            try { status = await accounts.ResetPasswordAsync(req.token, req.password); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            if (status != ResetStatus.Ok)
                return Results.BadRequest(new ApiError("This reset link is invalid or has expired.", "invalid_token"));

            log.LogInformation("Password reset confirmed");
            return Results.Ok(new StatusResponse("ok"));
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Verify)
        .Produces<StatusResponse>(200)
        .Produces<ApiError>(400)
        .WithSummary("Set a new password from a reset link (revokes every session)");


        // ── Sessions ─────────────────────────────────────────────────────────
        group.MapPost("/logout-others", async (
            [FromBody] LogoutOthersRequest req,
            HttpContext         ctx,
            IUserService        userSvc,
            ILogger<Program>    log) =>
        {
            User? user = ctx.Items[ApiConsts.UserHttpContext] as User;

            string? username = user?.username;

            string? session = ctx.User.GetSessionKey();

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(session))
                return Results.Unauthorized();

            try { await userSvc.ClearOtherSessionsAsync(username, session); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            log.LogInformation("User {Username} cleared other sessions", username);
            return Results.NoContent();
        })
        .RequireAuthorization()
        .RequireRateLimiting(RateLimitPolicies.Session)
        .Produces(204)
        .Produces<ApiError>(400)
        .Produces(401);
    }

    private static IResult TooManyEmails() => Results.Json(
        new ApiError("Too many messages requested for this address. Try again later.", "email_rate_limited"),
        statusCode: 429);

    /// <summary>Asked for a code again before the cooldown lapsed. Carries the wait in both places a client looks.</summary>
    private static IResult TooSoon(int seconds)
    {
        var problem = Results.Json(
            new ApiError($"Wait {seconds}s before requesting another code.", "resend_too_soon"),
            statusCode: 429);

        return new RetryAfterResult(problem, seconds);
    }

    /// <summary>
    /// "alexander@gmail.com" becomes "al*****r@gmail.com". Shown on the confirmation screen
    /// so a mistyped address is recognisable at a glance without putting the whole thing on
    /// screen. Short local parts are masked entirely rather than half-revealed.
    /// </summary>
    private static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "";

        int at = email.IndexOf('@');
        if (at <= 0) return "***";

        string local  = email[..at];
        string domain = email[at..];

        if (local.Length <= 2) return new string('*', local.Length) + domain;
        if (local.Length <= 4) return $"{local[0]}{new string('*', local.Length - 1)}{domain}";

        return $"{local[..2]}{new string('*', local.Length - 3)}{local[^1]}{domain}";
    }

    private static bool IsAcceptablePassword(string? password) =>
        !string.IsNullOrWhiteSpace(password) &&
        password.Length >= MinPasswordLength &&
        password.Length <= MaxPasswordLength;

    private static bool IsValidEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email) &&
        email.Trim().Length <= 128 &&
        MailAddress.TryCreate(email.Trim(), out MailAddress? parsed) &&
        parsed.Host.Contains('.');

    /// <summary>
    /// Origin used to build reset links. App:PublicUrl wins; otherwise the deploy's
    /// DOMAIN; the request itself is the last resort (correct only because
    /// UseForwardedHeaders restores the original scheme/host behind nginx).
    /// </summary>
    /// <summary>
    /// Adds Retry-After to another result. The rate limiter sets this header on its own
    /// rejections, so a 429 from the cooldown that did NOT would be the odd one out, and a
    /// client written against the header would sit there with no idea how long to wait.
    /// </summary>
    private sealed class RetryAfterResult(IResult inner, int seconds) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = seconds.ToString();
            return inner.ExecuteAsync(httpContext);
        }
    }

    private static string PublicBaseUrl(HttpContext ctx, IConfiguration cfg)
    {
        string? configured = cfg["App:PublicUrl"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');

        string? domain = cfg["DOMAIN"];
        if (!string.IsNullOrWhiteSpace(domain)) return $"https://{domain}";

        return $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    }
}
