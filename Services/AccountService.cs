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

    Task<(VerifyStatus status, User? user)> VerifyEmailAsync(string email, string code);

    /// <summary>Null when no account owns the address — callers must still answer 202.</summary>
    Task<ResetTicket?> IssueResetTokenAsync(string email);

    /// <summary>Lets the reset page say "this link expired" before the user types a new password.</summary>
    Task<bool> IsResetTokenValidAsync(string token);

    Task<ResetStatus> ResetPasswordAsync(string token, string newPassword);
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

    private const int MaxCodeAttempts = 5;

    private sealed record VerificationRow(string code_hash, DateTime expires_at, short attempts);

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

    public async Task<(VerifyStatus status, User? user)> VerifyEmailAsync(string email, string code)
    {
        User? user = await FindByEmailAsync(email);

        if (user is null)            return (VerifyStatus.NotFound, null);
        if (user.email_verified)     return (VerifyStatus.AlreadyVerified, user);

        await using var conn = Connect();

        var row = await conn.QuerySingleOrDefaultAsync<VerificationRow>(
            "SELECT code_hash, expires_at, attempts FROM email_verifications WHERE user_id = @UserId",
            new { UserId = user.id });

        if (row is null || row.expires_at <= DateTime.UtcNow)
            return (VerifyStatus.Expired, null);

        if (row.attempts >= MaxCodeAttempts)
            return (VerifyStatus.TooManyAttempts, null);

        if (!FixedTimeEquals(row.code_hash, HashCode(user.id, code)))
        {
            await conn.ExecuteAsync(
                "UPDATE email_verifications SET attempts = attempts + 1 WHERE user_id = @UserId",
                new { UserId = user.id });

            log.LogWarning("Wrong verification code for user {UserId} (attempt {Attempt})",
                user.id, row.attempts + 1);

            return (VerifyStatus.Invalid, null);
        }

        await conn.ExecuteAsync("""
            UPDATE users SET email_verified = TRUE WHERE id = @UserId;
            DELETE FROM email_verifications WHERE user_id = @UserId;
            """, new { UserId = user.id });

        user.email_verified = true;
        log.LogInformation("E-mail verified for user {Username}", user.username);

        return (VerifyStatus.Ok, user);
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
