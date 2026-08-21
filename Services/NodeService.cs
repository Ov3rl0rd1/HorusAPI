using Dapper;
using HorusAPI.Models;
using Npgsql;

namespace HorusAPI.Services;

public interface INodeService
{
    /// <summary>Resolve an X-API-PASSWORD to the owning server id, or null if unknown.</summary>
    Task<int?> ResolveServerIdAsync(string apiPassword);

    Task RegisterAsync(int serverId, NodeRegisterRequest req);
    Task ApplyEventsAsync(int serverId, NodeEventsRequest req);
}

/// <summary>Where + how to reach a node's control agent (host + shared secret).</summary>
public sealed record NodeTarget(string Host, string AuthPassword);

/// <summary>
/// Server side of the node protocol. Nodes authenticate with their auth_password
/// (X-API-PASSWORD), report public parameters (POST /node/register), and push online
/// telemetry (POST /node/events). Users are identified across the whole protocol by
/// their <c>vpn_uuid</c>.
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

    public async Task RegisterAsync(int serverId, NodeRegisterRequest req)
    {
        const string sql = """
            UPDATE vpn_servers SET
                host                = @host,
                reality_public_key  = @reality_public_key,
                reality_short_ids   = @reality_short_ids,
                reality_server_name = @reality_server_name,
                reality_dest        = @reality_dest,
                vless_port          = @vless_port,
                hysteria_port       = @hysteria_port,
                hop                 = @hysteria_hop,
                obfs_password       = @hysteria_obfs_password,
                olcrtc_provider     = @olcrtc_provider,
                olcrtc_transport    = @olcrtc_transport,
                olcrtc_room_id      = @olcrtc_room_id,
                olcrtc_room_key     = @olcrtc_room_key,
                agent_version       = @agent_version,
                last_registered_at  = NOW()
            WHERE id = @Id
            """;

        await using var conn = Connect();
        await conn.ExecuteAsync(sql, new
        {
            Id                     = serverId,
            host                   = Trunc(req.host, 256),
            reality_public_key     = Trunc(req.reality_public_key, 128),
            reality_short_ids      = req.reality_short_ids ?? [],
            reality_server_name    = Trunc(req.reality_server_name, 256),
            reality_dest           = Trunc(req.reality_dest, 256),
            vless_port             = req.vless_port,
            hysteria_port          = req.hysteria_port,
            hysteria_obfs_password = req.hysteria_obfs,
            hysteria_hop           = req.hysteria_port_range,
            olcrtc_provider        = Trunc(req.olcrtc_provider, 32),
            olcrtc_transport       = Trunc(req.olcrtc_transport, 32),
            olcrtc_room_id         = Trunc(req.olcrtc_room_id, 256),
            olcrtc_room_key        = Trunc(req.olcrtc_room_key, 128),
            agent_version          = Trunc(req.agent_version, 32),
        });

        log.LogInformation("Node {ServerId} registered ({Host})", serverId, req.host);
    }

    public async Task ApplyEventsAsync(int serverId, NodeEventsRequest req)
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
    }

    private static string Trunc(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);
}
