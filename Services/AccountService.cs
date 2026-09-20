using Dapper;
using HorusAPI.Models;
using HorusAPI.Services.Auth_Handler;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using System.Security.Cryptography;
using System.Text;

namespace HorusAPI.Services;

public enum VerifyStatus { Ok, NotFound, AlreadyVerified, Expired, TooManyAttempts, Invalid }

public enum ResetStatus { Ok, InvalidOrExpired }

public enum ChangeEmailStatus { Ok, EmailTaken, AlreadyVerified, NotFound }

/// <summary>User + plaintext token; the token only ever exists in the outgoing e-mail.</summary>
public record ResetTicket(int userId, string username, string email, string token);

/// <summary>
/// E-mail confirmation codes and password-reset tokens. Both secrets are stored
/// hashed, and both are single-purpose rows that die on use.
/// </summary>
public interface IAccountService
{
    /// <summary>Creates (or replaces) the pending 6-digit code for a user and returns the plaintext code.</summary>
    Task<string> IssueVerificationCodeAsync(int userId);

    /// <summary>Looks up an account by e-mail, regardless of verification state.</summary>
    Task<User?> FindByEmailAsync(string email);

    /// <summary>
    /// Checks a confirmation code. <c>attemptsLeft</c> is how many wrong guesses remain before
    /// the code dies — the screen tells the user, and without it the client would have to
    /// count locally, which a page reload resets and a resend invalidates. It is meaningful
    /// only alongside <see cref="VerifyStatus.Invalid"/>; every other status returns 0.
    /// </summary>
    Task<(VerifyStatus status, User? user, int attemptsLeft)> VerifyEmailAsync(string email, string code);

    /// <summary>Null when no account owns the address — callers must still answer 202.</summary>
    Task<ResetTicket?> IssueResetTokenAsync(string email);

    /// <summary>Lets the reset page say "this link expired" before the user types a new password.</summary>
    Task<bool> IsResetTokenValidAsync(string token);

    Task<ResetStatus> ResetPasswordAsync(string token, string newPassword);

    // ── Unverified accounts ──────────────────────────────────────────────────

    /// <summary>
    /// Mints a ticket that proves the caller owns an UNVERIFIED account, and returns the
    /// plaintext. Issued only after a correct password, and it is NOT a session: it opens
    /// resend / change-address / confirm and nothing else. One live ticket per account.
    /// </summary>
    Task<string> IssuePendingTicketAsync(int userId);

    /// <summary>The unverified account behind a live ticket, or null. Verified accounts never match.</summary>
    Task<User?> FindByPendingTicketAsync(string token);

    /// <summary>
    /// How long until this account may be sent another code. Zero when it may be sent now.
    /// Separate from the per-address hourly quota: that one stops abuse, this one stops a
    /// user hammering the button and is the number the button counts down from.
    /// </summary>
    Task<TimeSpan> ResendCooldownRemainingAsync(int userId);

    /// <summary>
    /// How long the pending code is still good for, and how long until another may be sent.
    /// Both come off the same row, so the confirmation screen costs one query rather than two.
    /// Zero lifetime means there is no live code — the screen then shows no countdown rather
    /// than a wrong one.
    /// </summary>
    Task<(TimeSpan codeLifetime, TimeSpan resendCooldown)> PendingCodeTimingsAsync(int userId);

    /// <summary>
    /// Corrects the address on an unverified account. Also drops any pending code, so one
    /// mailed to the OLD address can never confirm the new one.
    /// </summary>
    Task<ChangeEmailStatus> ChangeEmailAsync(int userId, string newEmail);

    /// <summary>
    /// Deletes abandoned unverified accounts older than <paramref name="olderThan"/>, and only
    /// those with nothing whatsoever attached. Returns how many went.
    /// </summary>
    Task<int> DeleteStaleUnverifiedAsync(TimeSpan olderThan);

    /// <summary>Drops expired pending tickets. Pure housekeeping — an expired ticket is already refused.</summary>
    Task<int> PurgeExpiredTicketsAsync();
}

// Deliberately NOT [DapperAot]: reads the sessions VARCHAR(64)[] as a scalar
// string[] (unsupported by the AOT materializer) and its reset flow behaved
// differently under the generated interceptors — stays on classic Dapper.
public class AccountService(
    IConfiguration cfg,
    IMemoryCache cache,
    ILogger<AccountService> log) : IAccountService
{
    public static readonly TimeSpan CodeLifetime  = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromMinutes(60);

    /// <summary>
    /// A ticket is a convenience for finishing registration, not a login. Half an hour is
    /// long enough to read an e-mail and short enough that an abandoned tab stops mattering.
    /// </summary>
    public static readonly TimeSpan PendingTicketLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Minimum gap between two codes for one account. The per-address hourly quota
    /// (IAccountRateLimiter) is the abuse control; this is the "resend in 0:47" the button
    /// shows, and it also stops one impatient user burning all three hourly permits in
    /// five seconds and then being locked out for an hour.
    /// </summary>
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);

    private const int MaxCodeAttempts = 5;

    private sealed record VerificationRow(string code_hash, DateTime expires_at, short attempts);

    private sealed record TimingRow(DateTime sent_at, DateTime expires_at);

    private NpgsqlConnection Connect() => new(cfg.GetConnectionString("Postgres"));

    // ── E-mail confirmation ──────────────────────────────────────────────────

    public async Task<string> IssueVerificationCodeAsync(int userId)
    {
        const string sql = """
            INSERT INTO email_verifications (user_id, code_hash, expires_at, attempts, sent_at)
            VALUES (@UserId, @CodeHash, @ExpiresAt, 0, NOW())
            ON CONFLICT (user_id) DO UPDATE
            SET code_hash  = EXCLUDED.code_hash,
                expires_at = EXCLUDED.expires_at,
                attempts   = 0,
                sent_at    = NOW()
            """;

        string code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        await using var conn = Connect();
        await conn.ExecuteAsync(sql, new
        {
            UserId    = userId,
            CodeHash  = HashCode(userId, code),
            ExpiresAt = DateTime.UtcNow + CodeLifetime
        });

        return code;
    }

    public async Task<User?> FindByEmailAsync(string email)
    {
        const string sql = "SELECT * FROM users WHERE lower(email) = lower(@Email) LIMIT 1";

        await using var conn = Connect();
        return await conn.QuerySingleOrDefaultAsync<User>(sql, new { Email = email.Trim() });
    }

    public async Task<(VerifyStatus status, User? user, int attemptsLeft)> VerifyEmailAsync(string email, string code)
    {
        User? user = await FindByEmailAsync(email);

        if (user is null)            return (VerifyStatus.NotFound, null, 0);
        if (user.email_verified)     return (VerifyStatus.AlreadyVerified, user, 0);

        await using var conn = Connect();

        var row = await conn.QuerySingleOrDefaultAsync<VerificationRow>(
            "SELECT code_hash, expires_at, attempts FROM email_verifications WHERE user_id = @UserId",
            new { UserId = user.id });

        if (row is null || row.expires_at <= DateTime.UtcNow)
            return (VerifyStatus.Expired, null, 0);

        if (row.attempts >= MaxCodeAttempts)
            return (VerifyStatus.TooManyAttempts, null, 0);

        if (!FixedTimeEquals(row.code_hash, HashCode(user.id, code)))
        {
            await conn.ExecuteAsync(
                "UPDATE email_verifications SET attempts = attempts + 1 WHERE user_id = @UserId",
                new { UserId = user.id });

            log.LogWarning("Wrong verification code for user {UserId} (attempt {Attempt})",
                user.id, row.attempts + 1);

            // What the user sees on the screen. Counted from the row we just incremented,
            // so it is the server's number and survives a reload.
            int left = Math.Max(0, MaxCodeAttempts - (row.attempts + 1));
            return (VerifyStatus.Invalid, null, left);
        }

        await conn.ExecuteAsync("""
            UPDATE users SET email_verified = TRUE WHERE id = @UserId;
            DELETE FROM email_verifications WHERE user_id = @UserId;
            """, new { UserId = user.id });

        user.email_verified = true;
        log.LogInformation("E-mail verified for user {Username}", user.username);

        return (VerifyStatus.Ok, user, 0);
    }

    // ── Password reset ───────────────────────────────────────────────────────

    public async Task<ResetTicket?> IssueResetTokenAsync(string email)
    {
        User? user = await FindByEmailAsync(email);
        if (user is null) return null;

        // One live link per account: requesting a new one kills the previous.
        const string sql = """
            DELETE FROM password_resets WHERE user_id = @UserId AND used_at IS NULL;
            INSERT INTO password_resets (token_hash, user_id, expires_at)
            VALUES (@TokenHash, @UserId, @ExpiresAt);
            """;

        string token = GenerateToken();

        await using var conn = Connect();
        await conn.ExecuteAsync(sql, new
        {
            UserId    = user.id,
            TokenHash = Sha256Hex(token),
            ExpiresAt = DateTime.UtcNow + ResetLifetime
        });

        return new ResetTicket(user.id, user.username, user.email, token);
    }

    public async Task<bool> IsResetTokenValidAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        await using var conn = Connect();
        return await conn.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (
                SELECT 1 FROM password_resets
                WHERE token_hash = @TokenHash AND used_at IS NULL AND expires_at > NOW())
            """, new { TokenHash = Sha256Hex(token) });
    }

    public async Task<ResetStatus> ResetPasswordAsync(string token, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(token)) return ResetStatus.InvalidOrExpired;

        string tokenHash = Sha256Hex(token);

        await using var conn = Connect();

        int? userId = await conn.QuerySingleOrDefaultAsync<int?>("""
            SELECT user_id FROM password_resets
            WHERE token_hash = @TokenHash AND used_at IS NULL AND expires_at > NOW()
            """, new { TokenHash = tokenHash });

        if (userId is null) return ResetStatus.InvalidOrExpired;

        // Read the live sessions before wiping them — they are the cache keys.
        // Via the User type (a plain scalar string[] read trips the project-wide
        // Dapper.AOT analyzer, DAP037); classic-mapped since this class isn't [DapperAot].
        string[]? sessions = (await conn.QuerySingleOrDefaultAsync<User>(
            "SELECT * FROM users WHERE id = @UserId", new { UserId = userId }))?.sessions;

        // A completed reset proves the address belongs to the user, so it also
        // confirms the e-mail, and every existing session is logged out.
        await conn.ExecuteAsync("""
            UPDATE users
            SET password_hash  = @PasswordHash,
                sessions       = '{}',
                email_verified = TRUE
            WHERE id = @UserId;
            UPDATE password_resets SET used_at = NOW() WHERE token_hash = @TokenHash;
            """, new
        {
            UserId       = userId,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword, workFactor: 12),
            TokenHash    = tokenHash
        });

        foreach (string session in sessions ?? [])
            cache.Remove(SessionAuthHandler.SESSION_CACHE_PREFIX + session);

        log.LogInformation("Password reset completed for user {UserId}; {Count} session(s) revoked",
            userId, sessions?.Length ?? 0);

        return ResetStatus.Ok;
    }

    // ── Secrets ──────────────────────────────────────────────────────────────

    // ── Unverified accounts ──────────────────────────────────────────────────

    public async Task<string> IssuePendingTicketAsync(int userId)
    {
        // One live ticket per account: logging in again invalidates the last one, the
        // same rule password_resets follows.
        const string sql = """
            DELETE FROM pending_logins WHERE user_id = @UserId;
            INSERT INTO pending_logins (token_hash, user_id, expires_at)
            VALUES (@TokenHash, @UserId, @ExpiresAt);
            """;

        string token = GenerateToken();

        await using var conn = Connect();
        await conn.ExecuteAsync(sql, new
        {
            UserId    = userId,
            TokenHash = Sha256Hex(token),
            ExpiresAt = DateTime.UtcNow + PendingTicketLifetime
        });

        return token;
    }

    public async Task<User?> FindByPendingTicketAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        // email_verified = FALSE is part of the lookup, not a check afterwards: a ticket
        // must stop working the instant the account it belongs to is confirmed, including
        // when that happened in another tab.
        const string sql = """
            SELECT u.* FROM pending_logins p
            JOIN users u ON u.id = p.user_id
            WHERE p.token_hash = @TokenHash
              AND p.expires_at > NOW()
              AND u.email_verified = FALSE
            LIMIT 1
            """;

        await using var conn = Connect();
        return await conn.QuerySingleOrDefaultAsync<User>(sql, new { TokenHash = Sha256Hex(token.Trim()) });
    }

    public async Task<TimeSpan> ResendCooldownRemainingAsync(int userId) =>
        (await PendingCodeTimingsAsync(userId)).resendCooldown;

    public async Task<(TimeSpan codeLifetime, TimeSpan resendCooldown)> PendingCodeTimingsAsync(int userId)
    {
        await using var conn = Connect();
        var row = await conn.QuerySingleOrDefaultAsync<TimingRow>(
            "SELECT sent_at, expires_at FROM email_verifications WHERE user_id = @UserId",
            new { UserId = userId });

        if (row is null) return (TimeSpan.Zero, TimeSpan.Zero);

        var now = DateTime.UtcNow;
        return (Remaining(AsUtc(row.expires_at) - now),
                Remaining(ResendCooldown - (now - AsUtc(row.sent_at))));

        // Npgsql hands back Unspecified for timestamptz read into DateTime; the values are
        // UTC, and subtracting them from DateTime.UtcNow without saying so is an hour or
        // three of silent drift depending on where the server stands.
        static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
        static TimeSpan Remaining(TimeSpan span) => span > TimeSpan.Zero ? span : TimeSpan.Zero;
    }

    public async Task<ChangeEmailStatus> ChangeEmailAsync(int userId, string newEmail)
    {
        await using var conn = Connect();

        try
        {
            // The WHERE clause carries the whole rule: an account that got confirmed while
            // this request was in flight matches nothing and changes nothing, so a
            // confirmed address can never be moved by this path.
            int changed = await conn.ExecuteAsync("""
                UPDATE users SET email = @Email
                WHERE id = @UserId AND email_verified = FALSE;
                """, new { UserId = userId, Email = newEmail.Trim() });

            if (changed == 0)
            {
                bool exists = await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM users WHERE id = @UserId)", new { UserId = userId });
                return exists ? ChangeEmailStatus.AlreadyVerified : ChangeEmailStatus.NotFound;
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // users_email_lower_key — one account per address.
            return ChangeEmailStatus.EmailTaken;
        }

        // Kill the outstanding code. It was mailed to the OLD address, and letting it
        // still confirm would mean whoever holds the old mailbox can verify the new one.
        await conn.ExecuteAsync("DELETE FROM email_verifications WHERE user_id = @UserId",
            new { UserId = userId });

        log.LogInformation("User {UserId} corrected their e-mail address before confirming", userId);
        return ChangeEmailStatus.Ok;
    }

    public async Task<int> DeleteStaleUnverifiedAsync(TimeSpan olderThan)
    {
        // Every FK into users is ON DELETE CASCADE, so a row deleted here takes its
        // subscriptions, payments and grants with it silently. That is precisely why the
        // guards below are explicit and generous rather than clever: this query must be
        // incapable of touching an account anyone has ever done anything with.
        //
        // An unverified account cannot normally hold a session (login refuses one) or a
        // subscription (checkout needs a session), so in a healthy system each NOT EXISTS
        // is redundant. They are here for the unhealthy system — a grandfathered row, a
        // hand-made admin grant, a future endpoint that forgets this rule.
        const string sql = """
            DELETE FROM users u
            WHERE u.email_verified = FALSE
              AND u.is_admin       = FALSE
              AND u.created_at     < NOW() - @Age::interval
              AND COALESCE(cardinality(u.sessions), 0) = 0
              AND u.current_server_id IS NULL
              AND u.expires_at        IS NULL
              AND NOT EXISTS (SELECT 1 FROM subscriptions        s WHERE s.user_id = u.id)
              AND NOT EXISTS (SELECT 1 FROM payments             p WHERE p.user_id = u.id)
              AND NOT EXISTS (SELECT 1 FROM slot_holds           h WHERE h.user_id = u.id)
              AND NOT EXISTS (SELECT 1 FROM plan_grants          g WHERE g.user_id = u.id)
              AND NOT EXISTS (SELECT 1 FROM promo_redemptions    r WHERE r.user_id = u.id)
            """;

        await using var conn = Connect();
        int removed = await conn.ExecuteAsync(sql, new { Age = $"{(int)olderThan.TotalMinutes} minutes" });

        if (removed > 0)
            log.LogInformation("Swept {Count} abandoned unverified account(s) older than {Age}", removed, olderThan);

        return removed;
    }

    public async Task<int> PurgeExpiredTicketsAsync()
    {
        await using var conn = Connect();
        return await conn.ExecuteAsync("DELETE FROM pending_logins WHERE expires_at <= NOW()");
    }

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);

        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');   // URL-safe: it travels in a link
    }

    /// <summary>The user id salts the code so two users cannot share a hash for the same 6 digits.</summary>
    private static string HashCode(int userId, string code) => Sha256Hex($"{userId}:{code}");

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
