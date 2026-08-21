using Dapper;
using HorusAPI.Models;
using Npgsql;

namespace HorusAPI.Services;

public interface IVpnServerService
{
    /// <summary>Candidate nodes for the client to TCP-ping: least-loaded with free capacity, one per country.</summary>
    Task<IEnumerable<PingCandidate>> GetPingCandidatesAsync();

    /// <summary>Everything needed to render a user's links for one node (+ its shared secret).</summary>
    Task<ServerRow?> GetConnectDataAsync(int serverId);
}

[DapperAot]   // hot read path — compile-time materializers, no reflection
public class VpnServerService(IConfiguration cfg, ILogger<VpnServerService> log) : IVpnServerService
{
    // Column list backing a ServerRow — `id` is aliased to the record's `server_id`.
    private const string ConnectColumns =
        "id AS server_id, name, country, city, host, auth_password, " +
        "reality_public_key, reality_short_ids, reality_server_name, reality_dest, " +
        "vless_port, hysteria_port, obfs_password, hop, " +
        "olcrtc_provider, olcrtc_transport, olcrtc_room_id, olcrtc_room_key, agent_version";

    private NpgsqlConnection Connect() => new(cfg.GetConnectionString("Postgres"));

    public async Task<IEnumerable<PingCandidate>> GetPingCandidatesAsync()
    {
        // One row per country (the least-loaded node there that still has a slot),
        // then the whole list ordered least-loaded first.
        const string sql = """
            SELECT id, country, city, host, current_load, reserved_count, max_clients
            FROM (
                SELECT DISTINCT ON (country)
                       id, country, city, host, current_load, reserved_count, max_clients
                FROM vpn_servers
                WHERE is_active AND reserved_count < max_clients
                ORDER BY country, reserved_count ASC, id
            ) c
            ORDER BY reserved_count ASC, country
            """;

        await using var conn = Connect();
        return await conn.QueryAsync<PingCandidate>(sql);
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
                log.LogWarning("Server {ServerId} not found or inactive", serverId);

            return row;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "DB error fetching connect data for server {ServerId}", serverId);
            throw;
        }
    }
}
