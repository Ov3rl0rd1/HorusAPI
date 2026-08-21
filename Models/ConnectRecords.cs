namespace HorusAPI.Models;

// ── Server selection (GET /servers, POST /servers/select) ─────────────────────

/// <summary>
/// One candidate node for the client to TCP-ping and choose from. Least-loaded
/// nodes with free capacity, one per country. <c>reserved_count</c>/<c>max_clients</c>
/// let the client show fullness; <c>current_load</c> is the live online count.
/// </summary>
public record PingCandidate(
    int    id,
    string country,
    string city,
    string host,
    int    current_load,
    int    reserved_count,
    int    max_clients);

/// <summary>Body of POST /servers/select. Omit <c>server_id</c> to auto-pick the least-loaded node.</summary>
public record SelectServerRequest(int? server_id);

/// <summary>The node a user is currently bound to (their reserved slot).</summary>
public record BoundServer(int id, string name, string country, string city, string host);

// ── Connection (GET /connect, header path → JSON) ─────────────────────────────

/// <summary>olcRTC parameters for the Horus app (whitelist bypass). Only returned to
/// the app (session in the X-Session-Key header), never in the third-party subscription.</summary>
public record OlcRtc(
    string provider,
    string transport,
    string room_id,
    string room_key,
    string uuid,
    string host);

/// <summary>
/// App-facing connect payload. <c>vless</c> lists every VLESS variant the node exposes
/// (one today), <c>hysteria2</c> is the Hysteria2 link, <c>olcrtc</c> is present only
/// when the node advertises an olcRTC room.
/// </summary>
public record ConnectResponse(
    BoundServer     server,
    string[]        vless,
    string          hysteria2,
    OlcRtc?         olcrtc);
