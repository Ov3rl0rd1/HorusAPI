using Dapper;
using Npgsql;
using HorusAPI.Models;

namespace HorusAPI.Services;

public interface IVpnServerService
{
    Task<IEnumerable<ServerListItem>> GetAvailableServersAsync();
    Task<ConnectData?>                GetConnectDataAsync(int serverId);
}

public class VpnServerService(IConfiguration cfg, ILogger<VpnServerService> log) : IVpnServerService
{
    private NpgsqlConnection Connect() =>
        new(cfg.GetConnectionString("Postgres"));

    public async Task<IEnumerable<ServerListItem>> GetAvailableServersAsync()
    {
        const string sql = """
            SELECT id, name, country, city, host, protocol,
                   current_load, max_clients, is_active
            FROM vpn_servers
            WHERE is_active = true
            ORDER BY current_load ASC, max_clients
            """;

        await using var conn = Connect();
        var rows = await conn.QueryAsync(sql);

        return rows.Select(r => new ServerListItem(
            id:          (int)r.id,
            name:        (string)r.name,
            country:     (string)r.country,
            city:        (string)r.city,
            host:        (string)r.host,
            protocol:    (string)r.protocol,
            current_load: (int)r.current_load,
            max_clients:   (int)r.max_clients,
            is_active: (bool)r.is_active)
            );
    }

    public async Task<ConnectData?> GetConnectDataAsync(int serverId)
    {
        // We join with server_keys to get the server's pre-shared public key / config.
        const string sql = """
            SELECT s.id, s.host, s.port, s.protocol,
                   k.pre_shared_key, k.client_config_template
            FROM vpn_servers s
            JOIN server_keys k ON k.server_id = s.id
            WHERE s.id = @ServerId AND s.is_active = true
            LIMIT 1
            """;

        try
        {
            await using var conn = Connect();
            var row = await conn.QuerySingleOrDefaultAsync(sql, new { ServerId = serverId });

            if (row is null)
            {
                log.LogWarning("Server {ServerId} not found or inactive", serverId);
                return null;
            }

            return new ConnectData(
                ServerId:     (int)row.id,
                Host:         (string)row.host,
                Port:         (int)row.port,
                Protocol:     (string)row.protocol,
                PreSharedKey: (string)row.pre_shared_key,
                ClientConfig: (string)row.client_config_template);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "DB error fetching connect data for server {ServerId}", serverId);
            throw;
        }
    }
}
