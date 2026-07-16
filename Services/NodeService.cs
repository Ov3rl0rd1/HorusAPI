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

    Task<Guid> EnsureUuidAsync(int userId);

    Task<(string host, string auth_password)> GetNodeTargetAsync(int serverId);
}

/// <summary>An active node reachable for a central → node control push.</summary>
public sealed record NodeTarget(string Host, string AuthPassword);

/// <summary>
/// Server side of the node protocol. Nodes authenticate with their auth_password
/// (X-API-PASSWORD), report public parameters, pull the authorized-user list, and
/// report online telemetry.
/// </summary>
public class NodeService(IConfiguration cfg, ILogger<NodeService> log) : INodeService
{
    public const string XRAY_USER_IDENTITY = "@horus";

    private NpgsqlConnection Connect() => new(cfg.GetConnectionString("Postgres"));

    /// <summary>The stable xray identity for a user across every protocol on every node.</summary>
    public static string EmailFor(string username) => $"{username}{XRAY_USER_IDENTITY}";

    public static string? UsernameFrom(string email) => email.EndsWith(XRAY_USER_IDENTITY) ? email.AsSpan(0, email.Length - XRAY_USER_IDENTITY.Length).ToString() : null;

    public async Task<int?> ResolveServerIdAsync(string apiPassword)
    {
        if (string.IsNullOrWhiteSpace(apiPassword))
            return null;

        const string sql = "SELECT id FROM vpn_servers WHERE auth_password = @Pw LIMIT 1";
        await using var conn = Connect();
        var id = await conn.ExecuteScalarAsync<int?>(sql, new { Pw = apiPassword });
        return id;
    }

    public async Task RegisterAsync(int serverId, NodeRegisterRequest req)
    {
        const string sql = """
            UPDATE vpn_servers SET
                host                = @host,
                protocol            = @protocol,
                reality_public_key  = @reality_public_key,
                reality_short_ids   = @reality_short_ids,
                reality_server_name = @reality_server_name,
                reality_dest        = @reality_dest,
                vless_port          = @vless_port,
                hysteria_port       = @hysteria_port,
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
            Id                  = serverId,
            host                = Trunc(req.host, 256),
            reality_public_key  = Trunc(req.reality_public_key, 128),
            reality_short_ids   = req.reality_short_ids ?? [],
            reality_server_name = Trunc(req.reality_server_name, 256),
            reality_dest        = Trunc(req.reality_dest, 256),
            vless_port          = req.vless_port,
            hysteria_port       = req.hysteria_port,
            olcrtc_provider     = Trunc(req.olcrtc_provider, 32),
            olcrtc_transport    = Trunc(req.olcrtc_transport, 32),
            olcrtc_room_id      = Trunc(req.olcrtc_room_id, 256),
            olcrtc_room_key     = Trunc(req.olcrtc_room_key, 128),
            agent_version       = Trunc(req.agent_version, 32),
        });

        log.LogInformation("Node {ServerId} registered ({Host})",
            serverId, req.host);
    }

    public async Task ApplyEventsAsync(int serverId, NodeEventsRequest req)
    {
        const string sql = "UPDATE vpn_servers SET current_load = @Load WHERE id = @Id";
        await using var conn = Connect();
        await conn.ExecuteAsync(sql, new { Load = Math.Max(0, req.provisioned_count), Id = serverId });

        foreach (var e in req.events)
        {
            string? username = UsernameFrom(e.email);
            if (username == null)
            {
                log.LogError("Can not get username from email {email} for event type {eventType} ({reason}) on server {serverId}", 
                    e.email, e.type, e.reason, serverId);
                continue;
            }

            const string updateUserSql = "UPDATE users SET last_disconnect_at = @LastDisconnect, reason = @Reason WHERE username = @Username";
            await conn.ExecuteAsync(updateUserSql, new { LastDisconnect = e.at, Reason = e.reason, Username = username });
        }

        if (req.events is { Length: > 0 })
            log.LogInformation("Node {ServerId}: {Online} online, {Events} event(s)",
                serverId, req.online_count, req.events.Length);
    }

    public async Task<Guid> EnsureUuidAsync(int userId)
    {
        const string sql = "FROM users SELECT vpn_uuid WHERE id = @Id";
        await using var conn = Connect();
        Guid uuid = await conn.ExecuteScalarAsync<Guid>(sql, new { Id = userId });

        return uuid;
    }

    public async Task<(string host, string auth_password)> GetNodeTargetAsync(int serverId)
    {
        const string sql = "FROM vpn_servers SELECT host, auth_password WHERE id = @Id";
        await using var conn = Connect();
        (string host, string auth_password) serverIdentity = await conn.QuerySingleAsync(sql, new { Id = serverId });

        return serverIdentity;
    }

    private static string Trunc(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);
}
