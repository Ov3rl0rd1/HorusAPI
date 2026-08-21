namespace HorusAPI.Models;

// Wire DTOs for the /node endpoints. Field names are snake_case to match exactly
// what the node agent (Horus-ServerInstance) sends and expects.

/// <summary>POST /node/register — a node reports its public parameters.</summary>
public record NodeRegisterRequest(
    string   host,
    string   reality_public_key,
    string[] reality_short_ids,
    string   reality_server_name,
    string   reality_dest,
    int      vless_port,
    int      hysteria_port,
    string   hysteria_obfs,
    string   hysteria_port_range,
    string   olcrtc_provider,
    string   olcrtc_transport,
    string   olcrtc_room_id,
    string   olcrtc_room_key,
    string   agent_version);

public record NodeRegisterResponse(int server_id);

/// <summary>A connect/disconnect observation reported by a node. Users are identified
/// by their <c>vpn_uuid</c> (the same id used in the client's inbound entry).</summary>
public record NodeUserEvent(string uuid, string type, string reason, DateTimeOffset at);

/// <summary>POST /node/events — batched online telemetry from a node. <c>online_count</c>
/// is the live connected count (drives vpn_servers.current_load).</summary>
public record NodeEventsRequest(int provisioned_count, int online_count, NodeUserEvent[] events);
