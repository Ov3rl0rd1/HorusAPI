namespace HorusAPI.Models;

// Admin view of a node. What a node exposes is decided by its xray profile, so `profile`
// (plus last_registered_at) is what tells an admin whether the node has checked in and what
// it is running. For the full picture — assignment vs reality, hashes, render errors — see
// GET /admin/servers/profiles.
public record ServerAdminItem(
    int       id,
    string    name,
    string    country,
    string    city,
    string    host,
    int       current_load,
    int       max_clients,
    int       max_reservations,
    bool      is_active,
    string    auth_password,
    string?   masquerade_url,
    string    profile,
    string    agent_version,
    DateTime? last_registered_at);
