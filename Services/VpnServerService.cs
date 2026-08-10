using Dapper;
using HorusAPI.Models;
using Npgsql;

namespace HorusAPI.Services;

public interface IVpnServerService
{
    Task<IEnumerable<ServerListItem>> GetAvailableServersAsync();
    Task<IEnumerable<BestServerItem>> GetBestServersAsync();
    Task<ServerRow?>                GetConnectDataAsync(int serverId);
    Task<ServerRow?> ChooseServerAsync(User user);
    Task BindUserAsync(int userId, int serverId);
}

[DapperAot]   // compile-time command/materializer generation + mismatch diagnostics
public class VpnServerService(IConfiguration cfg, ILogger<VpnServerService> log) : IVpnServerService
{
    // Column list backing a ServerRow — `id` is aliased to the record's `server_id`.
    private const string ConnectColumns =
        "id AS server_id, host, auth_password, reality_public_key, reality_short_ids, reality_server_name, " +
        "reality_dest, vless_port, hysteria_port, obfs_password, hop, " +
        "olcrtc_provider, olcrtc_transport, olcrtc_room_id, olcrtc_room_key, agent_version";

    private NpgsqlConnection Connect() => new(cfg.GetConnectionString("Postgres"));

    public async Task<IEnumerable<ServerListItem>> GetAvailableServersAsync()
    {
        const string sql = """
            SELECT id, name, country, city, host,
                   current_load, max_clients, is_active
            FROM vpn_servers
            WHERE is_active = true
            ORDER BY current_load ASC, max_clients
            """;

        await using var conn = Connect();
        return await conn.QueryAsync<ServerListItem>(sql);
    }

    public async Task<IEnumerable<BestServerItem>> GetBestServersAsync()
    {
        const string sql = """
            SELECT id, name, country, city, host, current_load, max_clients
            FROM vpn_servers
            WHERE is_active = true AND current_load < max_clients
            ORDER BY current_load ASC
            LIMIT 2
            """;

        await using var conn = Connect();
        return await conn.QueryAsync<BestServerItem>(sql);
    }

    public async Task<ServerRow?> GetConnectDataAsync(int serverId)
    {
        const string sql = $"""
            SELECT {ConnectColumns}
            FROM vpn_servers
            WHERE id = @ServerId AND is_active = true
            LIMIT 1
            """;

        try
        {
            await using var conn = Connect();
            ServerRow? row = await conn.QuerySingleOrDefaultAsync<ServerRow>(sql, new { ServerId = serverId });

            if (row is null)
            {
                log.LogWarning("Server {ServerId} not found or inactive", serverId);
                return null;
            }

            return row;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "DB error fetching connect data for server {ServerId}", serverId);
            throw;
        }
    }

    public async Task<ServerRow?> ChooseServerAsync(User user)
    {
        if (user.current_server_id.HasValue)
            return await GetConnectDataAsync(user.current_server_id.Value);

        return await GetConnectDataAsync((await GetBestServersAsync()).First().id);
    }

    public async Task BindUserAsync(int userId, int serverId)
    {
        const string sql = """
            UPDATE users
            SET current_server_id = @ServerId
            WHERE id = @UserId AND is_active = true
            """;

        try
        {
            await using var conn = Connect();
            await conn.ExecuteAsync(sql, new { UserId = userId, ServerId = serverId });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "DB error fetching connect data for server {userId}", userId);
            throw;
        }
    }
}