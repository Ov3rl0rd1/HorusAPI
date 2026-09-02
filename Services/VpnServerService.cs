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
    // `offers` comes back as text: it is handed to the client almost as-is, so there is no
    // reason to parse it into a typed model this API deliberately does not have.
    private const string ConnectColumns =
        "id AS server_id, name, country, city, host, auth_password, profile, offers::text AS offers";

    private NpgsqlConnection Connect() => new(cfg.GetConnectionString("Postgres"));

    public async Task<IEnumerable<PingCandidate>> GetPingCandidatesAsync()
    {
        // One row per country (the least-loaded node there that still has a slot),
        // then the whole list ordered least-loaded first. Candidates are filtered on the
        // HARD cap (max_reservations); max_clients rides along only so the client can flag a
        // node as heavily loaded (reserved_count ≥ max_clients) while it stays selectable.
        const string sql = """
            SELECT id, country, city, host, current_load, reserved_count, max_clients, max_reservations
            FROM (
                SELECT DISTINCT ON (country)
                       id, country, city, host, current_load, reserved_count, max_clients, max_reservations
                FROM vpn_servers
                WHERE is_active AND reserved_count < max_reservations
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
