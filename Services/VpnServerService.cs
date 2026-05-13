using Dapper;
using Npgsql;
using HorusAPI.Models;

namespace HorusAPI.Services;

public interface IVpnServerService
{
    Task<IEnumerable<ServerListItem>> GetAvailableServersAsync();
    Task<IEnumerable<BestServerItem>> GetBestServersAsync();
    Task<ConnectData?>                GetConnectDataAsync(int serverId);
}

public class VpnServerService(IConfiguration cfg, ILogger<VpnServerService> log) : IVpnServerService
{
    private NpgsqlConnection Connect() => new(cfg.GetConnectionString("Postgres"));

    public async Task<IEnumerable<ServerListItem>> GetAvailableServersAsync()
    {
        const string sql = """
            SELECT id, name, country, city, host, protocol,
                   current_load, max_clients, is_active, obfs_type, obfs_password, hop
            FROM vpn_servers
            WHERE is_active = true
            ORDER BY current_load ASC, max_clients
            """;

        await using var conn = Connect();
        var rows = await conn.QueryAsync(sql);

        return rows.Select(r => new ServerListItem(
            id:            (int)r.id,
            name:          (string)r.name,
            country:       (string)r.country,
            city:          (string)r.city,
            host:          (string)r.host,
            protocol:      (string)r.protocol,
            current_load:  (int)r.current_load,
            max_clients:   (int)r.max_clients,
            is_active:     (bool)r.is_active,
            obfs_type:     (string)r.obfs_type,
            obfs_password: (string)r.obfs_password,
            hop:           (string)r.hop));
    }

    public async Task<IEnumerable<BestServerItem>> GetBestServersAsync()
    {
        const string sql = """
            SELECT id, name, country, city, host, protocol, current_load, max_clients
            FROM vpn_servers
            WHERE is_active = true AND current_load < max_clients
            ORDER BY current_load ASC
            LIMIT 20
            """;

        await using var conn = Connect();
        var rows = await conn.QueryAsync(sql);

        return rows.Select(r => new BestServerItem(
            id:           (int)r.id,
            name:         (string)r.name,
            country:      (string)r.country,
            city:         (string)r.city,
            host:         (string)r.host,
            protocol:     (string)r.protocol,
            current_load: (int)r.current_load,
            max_clients:  (int)r.max_clients));
    }

    public async Task<ConnectData?> GetConnectDataAsync(int serverId)
    {
        const string sql = """
            SELECT id, host, protocol, obfs_type, obfs_password, hop
            FROM vpn_servers
            WHERE id = @ServerId AND is_active = true
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
                serverId:      (int)row.id,
                host:          (string)row.host,
                protocol:      (string)row.protocol,
                obfs_type:     (string)row.obfs_type,
                obfs_password: (string)row.obfs_password,
                hop:           (string)row.hop,
                template:      ApiConsts.CONFIG_TEMPLATE);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "DB error fetching connect data for server {ServerId}", serverId);
            throw;
        }
    }
}