namespace HorusAPI.Models;

// Admin view of a node. Aligned to the current vpn_servers schema: there is no
// `protocol` column (a node exposes all protocols); the reality_*/olcrtc_* fields are
// filled in by the node itself via POST /node/register. reality_public_key +
// last_registered_at let an admin see whether the node has registered yet.
public record ServerAdminItem(
    int              id,
    string           name,
    string           country,
    string           city,
    string           host,
    int              current_load,
    int              max_clients,
    int              max_reservations,
    bool             is_active,
    string           auth_password,
    string?          masquerade_url,
    string           reality_public_key,
    string           agent_version,
    DateTime?        last_registered_at);
