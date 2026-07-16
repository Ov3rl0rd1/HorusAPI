namespace HorusAPI.Models;

public record ServerRow(
    int    serverId,
    string host,
    string reality_public_key,
    string[] reality_short_ids,
    string reality_server_name,
    string reality_dest,
    int vless_port,
    int hysteria_port,
    string hysteria_auth,
    string hysteria_obfs,
    string hysteria_port_range,
    string olcrtc_provider,
    string olcrtc_transport,
    string olcrtc_room_id,
    string olcrtc_room_key,
    string agent_version);
