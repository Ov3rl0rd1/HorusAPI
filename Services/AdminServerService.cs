using Dapper;
using HorusAPI.Models;
using HorusAPI.Services.Auth_Handler;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace HorusAPI.Services;

public interface IAdminServerService
{
    Task<IEnumerable<ServerAdminItem>> GetAllServersAsync();
    Task<int> AddServerAsync(AddServerRequest req);
    Task<bool> RemoveServerAsync(int id);
    Task<IEnumerable<PingResult>> PingAllServersAsync();
    Task<bool> SetSubscriptionAsync(string username, DateTime expiresAt);
    Task<bool> ClearSubscriptionAsync(string username);
    Task<User?> GetByUsernameAsync(string username);
}

[DapperAot]   // compile-time command/materializer generation + mismatch diagnostics
public class AdminServerService(
    IConfiguration cfg,
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    ILogger<AdminServerService> log) : IAdminServerService
{
    // Column list backing a ServerAdminItem (names match the record parameters).
    private const string AdminColumns =
        "id, name, country, city, host, current_load, max_clients, is_active, " +
        "auth_password, masquerade_url, reality_public_key, agent_version, last_registered_at";

    private NpgsqlConnection Connect() => new(cfg.GetConnectionString("Postgres"));

    public async Task<IEnumerable<ServerAdminItem>> GetAllServersAsync()
    {
        const string sql = $"SELECT {AdminColumns} FROM vpn_servers ORDER BY id";

        await using var conn = Connect();
        return await conn.QueryAsync<ServerAdminItem>(sql);
    }

    public async Task<int> AddServerAsync(AddServerRequest req)
    {
        // Only identity + capacity + the shared secret. The node fills in
        // reality_*/olcrtc_*/ports itself via POST /node/register; the rest default.
        const string sql = """
            INSERT INTO vpn_servers
                (name, country, city, host, max_clients, auth_password, masquerade_url)
            VALUES
                (@name, @country, @city, @host, @max_clients,
                 COALESCE(@auth_password, ''), @masquerade_url)
            RETURNING id
            """;

        await using var conn = Connect();
        return await conn.ExecuteScalarAsync<int>(sql, req);
    }

    public async Task<bool> RemoveServerAsync(int id)
    {
        const string sql = "DELETE FROM vpn_servers WHERE id = @Id";
        await using var conn = Connect();
        return await conn.ExecuteAsync(sql, new { Id = id }) > 0;
    }

    public async Task<IEnumerable<PingResult>> PingAllServersAsync()
    {
        var servers = (await GetAllServersAsync()).ToList();
        var http = httpClientFactory.CreateClient("ping");

        var tasks = servers.Select(async s =>
        {
            string url = !string.IsNullOrEmpty(s.masquerade_url)
                ? s.masquerade_url
                : $"https://{s.host}";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                return new PingResult(s.id, s.name, true, (int)resp.StatusCode, null);
            }
            catch (Exception ex)
            {
                log.LogInformation("Ping failed for server {Id} ({Url}): {Message}", s.id, url, ex.Message);
                return new PingResult(s.id, s.name, false, null, ex.Message);
            }
        });

        return await Task.WhenAll(tasks);
    }

    public async Task<bool> SetSubscriptionAsync(string username, DateTime expiresAt)
    {
        const string sql = "UPDATE users SET expires_at = @ExpiresAt WHERE username = @Username RETURNING *";
        await using var conn = Connect();

        try
        {
            User? user = await conn.QuerySingleOrDefaultAsync<User>(sql, new { ExpiresAt = expiresAt, Username = username });

            if (user == null)
                return false;
            else
                UpdateUserCache(user);

            return true;
        }
        catch (Exception ex)
        {
            log.LogError("SetSubscription failed for user {Username} : {Message}", username, ex.Message);
            return false;
        }
    }

    public async Task<bool> ClearSubscriptionAsync(string username)
    {
        const string sql = "UPDATE users SET expires_at = NULL WHERE username = @Username";
        await using var conn = Connect();

        try
        {
            User? user = await conn.QuerySingleOrDefaultAsync<User>(sql, new { Username = username });

            if (user == null)
                return false;
            else
                UpdateUserCache(user);

            return true;
        }
        catch (Exception ex)
        {
            log.LogError("ClearSubscription failed for user {Username} : {Message}", username, ex.Message);
            return false;
        }
    }

    public async Task<User?> GetByUsernameAsync(string username)
    {
        const string sql = "SELECT * FROM users WHERE username = @Username LIMIT 1";
        await using var conn = Connect();
        return await conn.QuerySingleOrDefaultAsync<User>(sql, new { Username = username });
    }

    private void UpdateUserCache(User user)
    {
        foreach (var e in user.sessions ?? [])
            cache.Set(SessionAuthHandler.SESSION_CACHE_PREFIX + e,
                      user,
                      new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) });
    }
}