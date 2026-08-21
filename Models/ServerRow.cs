namespace HorusAPI.Models;

/// <summary>Everything needed to render a user's links for one node, plus the shared
/// secret used to (de)provision the user on that node's agent.</summary>
public record ServerRow(
    int      server_id,
    string   name,
    string   country,
    string   city,
    string   host,
    string   auth_password,
    string   reality_public_key,
    string[] reality_short_ids,
    string   reality_server_name,
    string   reality_dest,
    int      vless_port,
    int      hysteria_port,
    string   obfs_password,
    string   hop,
    string   olcrtc_provider,
    string   olcrtc_transport,
    string   olcrtc_room_id,
    string   olcrtc_room_key,
    string   agent_version);
