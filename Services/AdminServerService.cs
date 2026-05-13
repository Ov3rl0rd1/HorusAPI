using Dapper;
using HorusAPI.Models;
using Npgsql;

namespace HorusAPI.Services;

public interface IAdminServerService
{
    Task<IEnumerable<ServerAdminItem>> GetAllServersAsync();
    Task<int> AddServerAsync(AddServerRequest req);
    Task<bool> RemoveServerAsync(int id);
    Task<IEnumerable<PingResult>> PingAllServersAsync();
    Task<bool> SetSubscriptionAsync(int userId, DateTime expiresAt);
    Task<bool> ClearSubscriptionAsync(int userId);
}

public class AdminServerService(
    IConfiguration cfg,
    IHttpClientFactory httpClientFactory,
    ILogger<AdminServerService> log) : IAdminServerService
{
    private NpgsqlConnection Connect() => new(cfg.GetConnectionString("Postgres"));

    public async Task<IEnumerable<ServerAdminItem>> GetAllServersAsync()
    {
        const string sql = """
            SELECT id, name, country, city, host, protocol,
                   current_load, max_clients, is_active,
                   obfs_type, obfs_password, auth_password, hop, masquerade_url
            FROM vpn_servers
            ORDER BY id
            """;

        await using var conn = Connect();
        var rows = await conn.QueryAsync(sql);

        return rows.Select(r => new ServerAdminItem(
            id:             (int)r.id,
            name:           (string)r.name,
            country:        (string)r.country,
            city:           (string)r.city,
            host:           (string)r.host,
            protocol:       (string)r.protocol,
            current_load:   (int)r.current_load,
            max_clients:    (int)r.max_clients,
            is_active:      (bool)r.is_active,
            obfs_type:      (string)r.obfs_type,
            obfs_password:  (string)r.obfs_password,
            hop:            (string)r.hop,
            masquerade_url: (string?)r.masquerade_url));
    }

    public async Task<int> AddServerAsync(AddServerRequest req)
    {
        const string sql = """
            INSERT INTO vpn_servers
                (name, country, city, host, protocol, max_clients,
                 obfs_type, obfs_password, auth_password, hop, masquerade_url)
            VALUES
                (@name, @country, @city, @host, @protocol, @max_clients,
                 @obfs_type, @obfs_password, @auth_password, @hop, @masquerade_url)
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

    public async Task<bool> SetSubscriptionAsync(int userId, DateTime expiresAt)
    {
        const string sql = "UPDATE users SET expires_at = @ExpiresAt WHERE id = @UserId";
        await using var conn = Connect();
        return await conn.ExecuteAsync(sql, new { ExpiresAt = expiresAt, UserId = userId }) > 0;
    }

    public async Task<bool> ClearSubscriptionAsync(int userId)
    {
        const string sql = "UPDATE users SET expires_at = NULL WHERE id = @UserId";
        await using var conn = Connect();
        return await conn.ExecuteAsync(sql, new { UserId = userId }) > 0;
    }
}