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
    Task<User?> GetByUsernameAsync(string username);

    // ── xray profiles ────────────────────────────────────────────────────────

    /// <summary>What every node is running versus what it was told to run.</summary>
    Task<IEnumerable<ServerProfileState>> GetProfileStatesAsync();

    /// <summary>The fleet-wide default profile, plus how many nodes override it.</summary>
    Task<FleetProfile> GetFleetProfileAsync();

    /// <summary>Set (or clear, with null/empty) the fleet-wide default profile.</summary>
    Task SetFleetProfileAsync(string? profile);

    /// <summary>Set (or clear) one node's profile override. False when the node does not exist.</summary>
    Task<bool> SetServerProfileAsync(int id, string? profile);
}

[DapperAot]   // compile-time command/materializer generation + mismatch diagnostics
public class AdminServerService(
    IConfiguration cfg,
    IHttpClientFactory httpClientFactory,
    ILogger<AdminServerService> log) : IAdminServerService
{
    // Column list backing a ServerAdminItem (names match the record parameters).
    private const string AdminColumns =
        "id, name, country, city, host, current_load, max_clients, max_reservations, is_active, " +
        "auth_password, masquerade_url, profile, agent_version, last_registered_at";

    private NpgsqlConnection Connect() => new(cfg.GetConnectionString("Postgres"));

    public async Task<IEnumerable<ServerAdminItem>> GetAllServersAsync()
    {
        const string sql = $"SELECT {AdminColumns} FROM vpn_servers ORDER BY id";

        await using var conn = Connect();
        return await conn.QueryAsync<ServerAdminItem>(sql);
    }

    public async Task<int> AddServerAsync(AddServerRequest req)
    {
        // Only identity + capacity + the shared secret. Everything about what the node
        // actually serves — its profile and client offers — is reported by the node itself
        // via POST /node/register; the rest default.
        // max_reservations is the hard cap; default it to ceil(max_clients * 1.5) so the
        // soft (max_clients) and hard limits stay in the intended ~2:3 ratio.
        const string sql = """
            INSERT INTO vpn_servers
                (name, country, city, host, max_clients, max_reservations, auth_password, masquerade_url)
            VALUES
                (@name, @country, @city, @host, @max_clients, @MaxReservations,
                 COALESCE(@auth_password, ''), @masquerade_url)
            RETURNING id
            """;

        int maxReservations = req.max_reservations ?? (int)Math.Ceiling(req.max_clients * 1.5);

        await using var conn = Connect();
        return await conn.ExecuteScalarAsync<int>(sql, new
        {
            req.name, req.country, req.city, req.host, req.max_clients,
            MaxReservations = maxReservations, req.auth_password, req.masquerade_url
        });
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

    public async Task<User?> GetByUsernameAsync(string username)
    {
        const string sql = "SELECT * FROM users WHERE username = @Username LIMIT 1";
        await using var conn = Connect();
        return await conn.QuerySingleOrDefaultAsync<User>(sql, new { Username = username });
    }

    // ── xray profiles ────────────────────────────────────────────────────────

    public async Task<IEnumerable<ServerProfileState>> GetProfileStatesAsync()
    {
        // assigned_profile is resolved in SQL exactly as NodeService resolves it for the node
        // itself, so the admin view and what a node is actually told can never disagree.
        // in_sync compares that against what the node reports it is running; false means it has
        // been told to switch and has not applied it yet.
        // The fleet default comes in as a scalar subquery, not a join: a CROSS JOIN against an
        // empty fleet_settings would return no rows at all and make the whole fleet vanish
        // from the admin view.
        const string sql = """
            WITH fleet AS (
                SELECT NULLIF((SELECT default_profile FROM fleet_settings WHERE id = 1), '') AS default_profile
            )
            SELECT s.id, s.name, s.host, s.is_active,
                   s.profile,
                   NULLIF(s.desired_profile, '')                                     AS desired_profile,
                   COALESCE(NULLIF(s.desired_profile, ''), f.default_profile)         AS assigned_profile,
                   (COALESCE(NULLIF(s.desired_profile, ''), f.default_profile, s.profile)
                        IS NOT DISTINCT FROM s.profile)                               AS in_sync,
                   s.profile_hash, s.config_hash,
                   jsonb_array_length(s.offers)                                       AS offer_count,
                   s.render_error, s.warnings, s.last_registered_at
            FROM vpn_servers s, fleet f
            ORDER BY s.id
            """;

        await using var conn = Connect();
        return await conn.QueryAsync<ServerProfileState>(sql);
    }

    public async Task<FleetProfile> GetFleetProfileAsync()
    {
        const string sql = """
            SELECT COALESCE((SELECT default_profile FROM fleet_settings WHERE id = 1), '') AS default_profile,
                   (SELECT COUNT(*) FROM vpn_servers)                                       AS nodes_total,
                   (SELECT COUNT(*) FROM vpn_servers WHERE COALESCE(desired_profile, '') <> '') AS nodes_overridden
            """;

        await using var conn = Connect();
        return await conn.QuerySingleAsync<FleetProfile>(sql);
    }

    public async Task SetFleetProfileAsync(string? profile)
    {
        // Upsert: the row is seeded by init.sql, but a database migrated by hand may not have it.
        const string sql = """
            INSERT INTO fleet_settings (id, default_profile, updated_at)
            VALUES (1, @Profile, NOW())
            ON CONFLICT (id) DO UPDATE SET default_profile = @Profile, updated_at = NOW()
            """;

        var value = Normalize(profile);

        await using var conn = Connect();
        await conn.ExecuteAsync(sql, new { Profile = value });

        log.LogWarning("Fleet default profile set to '{Profile}' — every node without an override will switch",
            value.Length == 0 ? "(none)" : value);
    }

    public async Task<bool> SetServerProfileAsync(int id, string? profile)
    {
        // NULL, not '': a cleared override must fall through to the fleet default.
        const string sql = "UPDATE vpn_servers SET desired_profile = @Profile WHERE id = @Id";

        var value = Normalize(profile);

        await using var conn = Connect();
        var changed = await conn.ExecuteAsync(sql, new { Id = id, Profile = value.Length == 0 ? null : value }) > 0;

        if (changed)
            log.LogWarning("Node {ServerId} profile override set to '{Profile}'",
                id, value.Length == 0 ? "(fleet default)" : value);

        return changed;
    }

    /// <summary>Profile names are file names on the node — keep them boring.</summary>
    private static string Normalize(string? profile)
    {
        var value = (profile ?? "").Trim();
        return value.Length > 64 ? value[..64] : value;
    }

    /// <summary>
    /// True when a profile name could name a file in the node's catalogue. Rejects path
    /// separators and traversal outright: the name is joined onto a directory on every node,
    /// so anything else is a path-traversal attempt rather than a typo.
    /// </summary>
    public static bool IsValidProfileName(string? profile)
    {
        var value = (profile ?? "").Trim();
        if (value.Length == 0) return true;                       // clearing is always allowed
        if (value.Length > 64) return false;

        return value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
               && !value.Contains("..", StringComparison.Ordinal)
               && value[0] != '.';
    }
}