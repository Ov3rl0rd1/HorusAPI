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
            ILogger<Program> log) =>
        {
            if (string.IsNullOrWhiteSpace(req.username) ||
                string.IsNullOrWhiteSpace(req.password))
                return Results.BadRequest(new ApiError("Username and password are required."));

            User? user;
            string? session;

            try
            {
                user = await userSvc.AuthenticateAsync(req.username, req.password);
                if (user is null) return Results.Unauthorized();

                // Unverified accounts exist but cannot be used — the client should
                // route the user to the code screen on this code.
                if (!user.email_verified)
                    return Results.Json(
                        new ApiError("E-mail is not confirmed.", "email_unverified"), statusCode: 403);

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
            if (!IsValidEmail(req.email) || string.IsNullOrWhiteSpace(req.code))
                return Results.BadRequest(new ApiError("E-mail and code are required."));

            VerifyStatus status;
            User? user;
            try { (status, user) = await accounts.VerifyEmailAsync(req.email.Trim(), req.code.Trim()); }
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


        // ── Resend the code ──────────────────────────────────────────────────
        group.MapPost("/resend-code", async (
            [FromBody] ResendCodeRequest req,
            IAccountService     accounts,
            IEmailSender        mail,
            IAccountRateLimiter quota,
            ILogger<Program>    log) =>
        {
            if (!IsValidEmail(req.email))
                return Results.BadRequest(new ApiError("A valid e-mail address is required."));

            string email = req.email.Trim();

            // Charged for every address, real or not, so the 202/429 split never
            // reveals which addresses exist while still capping how many codes a
            // real, targeted address can be sent per hour across rotating IPs.
            if (!await quota.TryAcquireAsync(email))
                return TooManyEmails();

            try
            {
                User? user = await accounts.FindByEmailAsync(email);

                // Unknown or already-confirmed addresses get the same 202 as the rest.
                if (user is not null && !user.email_verified)
                {
                    string code = await accounts.IssueVerificationCodeAsync(user.id);
                    await mail.SendVerificationCodeAsync(email, user.username, code, AccountService.CodeLifetime);
                }
                else
                {
                    log.LogInformation("Resend requested for unknown or already-verified address");
                }
            }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            return Results.Json(
                new RegisterResponse("unverified", email, (int)AccountService.CodeLifetime.TotalSeconds),
                statusCode: 202);
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Email)
        .Produces<RegisterResponse>(202)
        .Produces<ApiError>(400)
        .Produces<ApiError>(429)
        .WithSummary("Mail a fresh confirmation code");


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
    private static string PublicBaseUrl(HttpContext ctx, IConfiguration cfg)
    {
        string? configured = cfg["App:PublicUrl"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');

        string? domain = cfg["DOMAIN"];
        if (!string.IsNullOrWhiteSpace(domain)) return $"https://{domain}";

        return $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    }
}
