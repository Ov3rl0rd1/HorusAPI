using Dapper;
using HorusAPI.Models;
using Npgsql;
using System.Security.Cryptography;

namespace HorusAPI.Services;

public interface IUserService
{
    Task<User?> AuthenticateAsync(string username, string password);
    Task<User?> AuthenticateSessionAsync(string username, string session);
    Task<string?> CreateSession(string username);
    Task<bool> CreateUserAsync(string username, string password, string email);
    Task ClearOtherSessionsAsync(string username, string currentSession);
}

public class UserService(IConfiguration cfg, ILogger<UserService> log) : IUserService
{
    private static string GenerateSession()
    {
        var bytes = new byte[48];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
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

    public async Task<User?> AuthenticateSessionAsync(string username, string session)
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

            // Bug fix: inverted – return null when session is NOT found
            if (user.sessions is null || !user.sessions.Contains(session))
            {
                log.LogWarning("Auth failed – session not valid: {Username}", username);
                return null;
            }

            return user;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "DB error during session auth for {Username}", username);
            throw;
        }
    }

    public async Task<string?> CreateSession(string username)
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
            WHERE username = @Username
            """;
        try
        {
            string session = GenerateSession();
            await using var conn = Connect();
            int rows = await conn.ExecuteAsync(sql, new { Username = username, Session = session });

            if (rows == 0)
            {
                log.LogWarning("CreateSession: user not found: {Username}", username);
                return null;
            }

            return session;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "DB error during CreateSession for {Username}", username);
            throw;
        }
    }

    public async Task<bool> CreateUserAsync(string username, string password, string email)
    {
        const string sql = """
            INSERT INTO users (username, password_hash, email, is_active)
            VALUES (@Username, @PasswordHash, @Email, TRUE)
            """;
        try
        {
            string hash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);
            await using var conn = Connect();
            await conn.ExecuteAsync(sql, new { Username = username, PasswordHash = hash, Email = email });
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // Unique constraint violation – username already taken
            return false;
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