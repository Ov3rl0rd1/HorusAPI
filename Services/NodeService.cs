using System.Text.Json.Nodes;
using Dapper;
using HorusAPI.Models;
using Npgsql;

namespace HorusAPI.Services;

public interface INodeService
{
    /// <summary>Resolve an X-API-PASSWORD to the owning server id, or null if unknown.</summary>
    Task<int?> ResolveServerIdAsync(string apiPassword);

    /// <summary>Record what a node reports it is running. Returns the profile it should be on.</summary>
    Task<string?> RegisterAsync(int serverId, NodeRegisterRequest req);

    /// <summary>Apply telemetry. Returns the profile the node should be on, as register does.</summary>
    Task<string?> ApplyEventsAsync(int serverId, NodeEventsRequest req);

    /// <summary>
    /// The profile a node should be running: its own override, else the fleet default.
    /// Null means "no opinion" — the node keeps whatever its own .env selects.
    /// </summary>
    Task<string?> ResolveAssignedProfileAsync(int serverId);
}

/// <summary>Where + how to reach a node's control agent (host + shared secret).</summary>
public sealed record NodeTarget(string Host, string AuthPassword);

/// <summary>
/// Server side of the node protocol. Nodes authenticate with their auth_password
/// (X-API-PASSWORD), report what they are running (POST /node/register), and push online
/// telemetry (POST /node/events). Users are identified across the whole protocol by
/// their <c>vpn_uuid</c>.
///
/// Both node-facing calls answer with the profile that node should be on, which is how a
/// protocol switch reaches the fleet: set it here, and every node picks it up on its next
/// telemetry post without anyone touching a server.
/// </summary>
public class NodeService(IConfiguration cfg, ILogger<NodeService> log) : INodeService
{
    private NpgsqlConnection Connect() => new(cfg.GetConnectionString("Postgres"));

    public async Task<int?> ResolveServerIdAsync(string apiPassword)
    {
        if (string.IsNullOrWhiteSpace(apiPassword))
            return null;

        const string sql = "SELECT id FROM vpn_servers WHERE auth_password = @Pw LIMIT 1";
        await using var conn = Connect();
        return await conn.ExecuteScalarAsync<int?>(sql, new { Pw = apiPassword });
    }

    public async Task<string?> RegisterAsync(int serverId, NodeRegisterRequest req)
    {
        // Only the profile-era columns are written. The pre-profile ones are left untouched
        // rather than blanked, so rolling this build back still finds the data it expects.
        const string sql = """
            UPDATE vpn_servers SET
                host               = @host,
                profile            = @profile,
                profile_hash       = @profile_hash,
                config_hash        = @config_hash,
                offers             = @offers::jsonb,
                warnings           = @warnings,
                render_error       = @render_error,
                agent_version      = @agent_version,
                last_registered_at = NOW()
            WHERE id = @Id
            """;

        var offers = NormalizeOffers(req.offers, serverId);

        await using var conn = Connect();
        await conn.ExecuteAsync(sql, new
        {
            Id            = serverId,
            host          = Trunc(req.host, 256),
            profile       = Trunc(req.profile, 64),
            profile_hash  = Trunc(req.profile_hash, 80),
            config_hash   = Trunc(req.config_hash, 80),
            offers,
            warnings      = req.warnings ?? [],
            render_error  = string.IsNullOrWhiteSpace(req.render_error) ? null : req.render_error,
            agent_version = Trunc(req.agent_version, 32),
        });

        if (!string.IsNullOrWhiteSpace(req.render_error))
            log.LogError("Node {ServerId} ({Host}) failed to render profile '{Profile}': {Error}",
                serverId, req.host, req.profile, req.render_error);
        else
            log.LogInformation("Node {ServerId} ({Host}) registered on profile '{Profile}' ({Hash})",
                serverId, req.host, req.profile, req.profile_hash);

        foreach (var warning in req.warnings ?? [])
            log.LogWarning("Node {ServerId}: {Warning}", serverId, warning);

        return await ResolveAssignedProfileAsync(serverId);
    }

    /// <summary>
    /// Offers must reach the database as a JSON array and nothing else — it is served back to
    /// clients almost verbatim, so a node sending a scalar or an object would otherwise put a
    /// shape the renderer cannot read into the column.
    /// </summary>
    private string NormalizeOffers(JsonNode? offers, int serverId)
    {
        if (offers is JsonArray array) return array.ToJsonString();

        if (offers is not null)
            log.LogError("Node {ServerId} sent offers that are not an array ({Kind}) — storing none",
                serverId, offers.GetType().Name);

        return "[]";
    }

    public async Task<string?> ApplyEventsAsync(int serverId, NodeEventsRequest req)
    {
        await using var conn = Connect();

        // current_load tracks the LIVE online count reported by the node.
        await conn.ExecuteAsync(
            "UPDATE vpn_servers SET current_load = @Load WHERE id = @Id",
            new { Load = Math.Max(0, req.online_count), Id = serverId });

        foreach (var e in req.events ?? [])
        {
            if (!Guid.TryParse(e.uuid, out _))
            {
                log.LogError("Node {ServerId}: event with unparseable uuid '{Uuid}' ({Type})", serverId, e.uuid, e.type);
                continue;
            }

            // Only disconnects carry a reason worth persisting; connects just update the timestamp.
            const string sql = """
                UPDATE users
                SET last_disconnect_at     = @At,
                    last_disconnect_reason  = @Reason
                WHERE vpn_uuid = @Uuid::uuid
                """;
            await conn.ExecuteAsync(sql, new
            {
                At     = e.at,
                Reason = Trunc(e.reason, 16),
                Uuid   = e.uuid
            });
        }

        if (req.events is { Length: > 0 })
            log.LogInformation("Node {ServerId}: {Online} online, {Events} event(s)",
                serverId, req.online_count, req.events.Length);

        return await ResolveAssignedProfileAsync(serverId);
    }

    public async Task<string?> ResolveAssignedProfileAsync(int serverId)
    {
        // A per-node override wins; otherwise the fleet default. Empty strings count as unset
        // so clearing either one in the admin UI does the obvious thing.
        const string sql = """
            SELECT COALESCE(
                NULLIF(s.desired_profile, ''),
                NULLIF((SELECT default_profile FROM fleet_settings WHERE id = 1), ''))
            FROM vpn_servers s
            WHERE s.id = @Id
            """;

        await using var conn = Connect();
        return await conn.ExecuteScalarAsync<string?>(sql, new { Id = serverId });
    }

    private static string Trunc(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);
}
