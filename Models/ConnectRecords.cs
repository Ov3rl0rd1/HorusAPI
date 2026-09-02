using System.Text.Json.Nodes;

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
    int    max_clients,       // soft "recommended" threshold (advisory load display)
    int    max_reservations); // hard capacity cap (physical limit)

/// <summary>Body of POST /servers/select. Omit <c>server_id</c> to auto-pick the least-loaded node.</summary>
public record SelectServerRequest(int? server_id);

/// <summary>The node a user is currently bound to (their reserved slot).</summary>
public record BoundServer(int id, string name, string country, string city, string host);

// ── Connection (GET /connect, header path → JSON) ─────────────────────────────

/// <summary>
/// One ready-to-use client outbound: a complete xray outbound object, exactly as the node
/// described it, with this user's uuid already substituted. The client can drop it straight
/// into a config.
///
/// Deliberately untyped. This API models no protocol at all, so a node can start offering a
/// new one and its users get a working config the same day, with no release on this side.
/// </summary>
public record ClientOutbound(
    string   id,        // stable id from the node's profile, e.g. "vless-reality"
    string   label,     // human-readable, for the client's UI
    string   tag,       // the node-side inbound tag this corresponds to
    JsonNode outbound); // a full xray outbound

/// <summary>
/// App-facing connect payload: the node the caller is bound to, and every outbound it offers
/// the app, in the order the node listed them (a profile lists its preferred one first).
/// </summary>
public record ConnectResponse(
    BoundServer      server,
    ClientOutbound[] outbounds);
