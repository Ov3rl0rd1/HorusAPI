using Dapper;
using Npgsql;
using HorusAPI.Models;
using BCrypt.Net;

namespace HorusAPI.Services;

public interface IUserService
{
    Task<User?> AuthenticateAsync(string username, string password);
}

public class UserService(IConfiguration cfg, ILogger<UserService> log) : IUserService
{
    private NpgsqlConnection Connect() =>
        new(cfg.GetConnectionString("Postgres"));

    public async Task<User?> AuthenticateAsync(string username, string password)
    {
        const string sql = """
            SELECT *
            FROM users
            WHERE username = @Username
            LIMIT 1
            """;

        try
        {
            await using var conn = Connect();
            var user = await conn.QuerySingleOrDefaultAsync<User>(sql,
                new { Username = username });

            if (user is null)
            {
                log.LogWarning("Auth failed – user not found: {Username}", username);
                return null;
            }

            //if (!user.IsActive)
            //{
            //    log.LogWarning("Auth failed – user inactive: {Username}", username);
            //    return null;
            //}

            if (user.expires_at.HasValue && user.expires_at.Value < DateTime.UtcNow)
            {
                log.LogWarning("Auth failed – subscription expired: {Username}", username);
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
}
