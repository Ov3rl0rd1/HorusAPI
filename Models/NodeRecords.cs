using System.Text.Json.Nodes;

namespace HorusAPI.Models;

// Wire DTOs for the /node endpoints. Field names are snake_case to match exactly
// what the node agent (Horus-ServerInstance) sends and expects.

/// <summary>
/// POST /node/register — a node reports what it is actually running.
///
/// There is nothing protocol-specific here on purpose. <c>offers</c> carries whole
/// client-side xray outbounds (each still holding a <c>${uuid}</c> placeholder), so this API
/// can hand a user a working config for a protocol nobody taught it about: it substitutes the
/// user and serves the JSON verbatim.
///
/// The pre-profile fields are still accepted, and nullable, purely so a node that has not been
/// upgraded yet keeps registering instead of failing. They are no longer read on /connect.
/// </summary>
public record NodeRegisterRequest(
    string    host,
    string    agent_version,

    // ── profile-era fields ───────────────────────────────────────────────────
    string?   profile = null,
    string?   profile_hash = null,
    string?   config_hash = null,
    JsonNode? offers = null,
    string[]? warnings = null,
    string?   render_error = null,

    // ── pre-profile fields (deprecated; accepted so old nodes still register) ─
    string?   reality_public_key = null,
    string[]? reality_short_ids = null,
    string?   reality_server_name = null,
    string?   reality_dest = null,
    int?      vless_port = null,
    int?      hysteria_port = null,
    string?   hysteria_obfs = null,
    string?   hysteria_port_range = null,
    string?   olcrtc_provider = null,
    string?   olcrtc_transport = null,
    string?   olcrtc_room_id = null,
    string?   olcrtc_room_key = null);

/// <summary>
/// Response to register. <c>assigned_profile</c> is the profile this node should be running;
/// null means "keep whatever you chose". This is the lever that switches a node's protocol
/// without touching the node.
/// </summary>
public record NodeRegisterResponse(int server_id, string? assigned_profile);

/// <summary>A connect/disconnect observation reported by a node. Users are identified
/// by their <c>vpn_uuid</c> (the same id used in the client's inbound entry).</summary>
public record NodeUserEvent(string uuid, string type, string reason, DateTimeOffset at);

/// <summary>POST /node/events — batched online telemetry from a node. <c>online_count</c>
/// is the live connected count (drives vpn_servers.current_load).</summary>
public record NodeEventsRequest(int provisioned_count, int online_count, NodeUserEvent[] events);

/// <summary>
/// Response to events, carrying the same assignment as register. Telemetry doubles as the
/// channel for a protocol switch, so a node that has been up for weeks picks one up on its
/// next post rather than only at restart.
/// </summary>
public record NodeEventsResponse(string? assigned_profile);

// ── Admin: profile assignment ────────────────────────────────────────────────

/// <summary>Body of PUT /admin/fleet/profile and PUT /admin/servers/{id}/profile.
/// A null or empty name clears the assignment (fall back to the fleet default, or to
/// whatever the node itself is configured with).</summary>
public record SetProfileRequest(string? profile);

/// <summary>The fleet-wide default profile plus how many nodes override it.</summary>
public record FleetProfile(string default_profile, int nodes_total, int nodes_overridden);

/// <summary>
/// What one node is running versus what it was told to run. <c>in_sync</c> false means the
/// node has not applied its assignment yet (it re-renders on its next update tick).
/// </summary>
public record ServerProfileState(
    int       id,
    string    name,
    string    host,
    bool      is_active,
    string    profile,            // what the node reports it is running
    string?   desired_profile,    // per-node override; null = follow the fleet default
    string?   assigned_profile,   // what that resolves to
    bool      in_sync,
    string    profile_hash,
    string    config_hash,
    int       offer_count,
    string?   render_error,
    string[]  warnings,
    DateTime? last_registered_at);
