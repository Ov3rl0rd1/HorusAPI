using Dapper;
using HorusAPI.Models;
using Npgsql;
using System.Security.Cryptography;

namespace HorusAPI.Services;

public enum CreateUserStatus { Created, UsernameTaken, EmailTaken }

/// <summary><c>userId</c> is only meaningful when <c>status</c> is <see cref="CreateUserStatus.Created"/>.</summary>
public record CreateUserResult(CreateUserStatus status, int userId);

public interface IUserService
{
    Task<User?> AuthenticateAsync(string username, string password);
    Task<string?> CreateSession(int userId);
    Task<CreateUserResult> CreateUserAsync(string username, string password, string email);
    Task ClearOtherSessionsAsync(string username, string currentSession);
}

public class UserService(IConfiguration cfg, ILogger<UserService> log) : IUserService
{
    private static string GenerateSession(int userId)
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return $"{userId}.{Convert.ToBase64String(bytes)}";
    }

    private NpgsqlConnection Connect() => new(cfg.GetConnectionString("Postgres"));

    public async Task<User?> AuthenticateAsync(string username, string password)
    {
        const string sql = """
            SELECT * FROM users WHERE username = @Username LIMIT 1
            """;
        try
        {
            await using var conn = Connect();
            var user = await conn.QuerySingleOrDefaultAsync<User>(sql, new { Username = username });

            if (user is null)
            {
                log.LogWarning("Auth failed – user not found: {Username}", username);
                return null;
            }

            if (!BCrypt.Net.BCrypt.Verify(password, user.password_hash))
            {
                log.LogWarning("Auth failed – wrong password: {Username}", username);
                return null;
            }

            return user;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "DB error during authentication for {Username}", username);
            throw;
        }
    }

    public async Task<string?> CreateSession(int userId)
    {
        // Keep sessions array bounded at 10: trim oldest when at cap before appending.
        const string sql = """
            UPDATE users
            SET sessions = array_append(
                CASE WHEN array_length(sessions, 1) >= 10
                     THEN sessions[array_length(sessions,1)-8 : array_length(sessions,1)]
                     ELSE sessions
                END,
                @Session)
            WHERE id = @uuid
            """;
        try
        {
            string session = GenerateSession(userId);
            await using var conn = Connect();
            int rows = await conn.ExecuteAsync(sql, new { uuid = userId, Session = session });

            if (rows == 0)
            {
                log.LogWarning("CreateSession: user not found: {UserId}", userId);
                return null;
            }

            return session;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "DB error during CreateSession for {UserId}", userId);
            throw;
        }
    }

    /// <summary>
    /// Creates an account in the unverified state: it cannot log in until
    /// POST /auth/verify accepts the 6-digit code mailed to <paramref name="email"/>.
    /// </summary>
    public async Task<CreateUserResult> CreateUserAsync(string username, string password, string email)
    {
        const string sql = """
            INSERT INTO users (username, password_hash, email, is_active, email_verified)
            VALUES (@Username, @PasswordHash, @Email, TRUE, FALSE)
            RETURNING id
            """;
        try
        {
            string hash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);
            await using var conn = Connect();
            int id = await conn.ExecuteScalarAsync<int>(sql, new { Username = username, PasswordHash = hash, Email = email.Trim() });
            return new CreateUserResult(CreateUserStatus.Created, id);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // Unique violation — either users_username_key or users_email_lower_key.
            bool email_taken = ex.ConstraintName?.Contains("email", StringComparison.OrdinalIgnoreCase) == true;
            return new CreateUserResult(
                email_taken ? CreateUserStatus.EmailTaken : CreateUserStatus.UsernameTaken, 0);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "DB error during CreateUser for {Username}", username);
            throw;
        }
    }

    public async Task ClearOtherSessionsAsync(string username, string currentSession)
    {
        const string sql = """
            UPDATE users
            SET sessions = ARRAY[@CurrentSession]::VARCHAR(64)[]
            WHERE username = @Username
            """;
        try
        {
            await using var conn = Connect();
            await conn.ExecuteAsync(sql, new { Username = username, CurrentSession = currentSession });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "DB error during ClearOtherSessions for {Username}", username);
            throw;
        }
    }
}