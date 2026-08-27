namespace HorusAPI.Models;

// What an admin supplies to add a node. Everything protocol-specific (reality keys,
// ports, olcrtc room) is reported by the node itself via POST /node/register, so the
// admin only needs identity + capacity + the shared secret.
public record AddServerRequest(
    string  name,
    string  country,
    string  city,
    string  host,
    int     max_clients,
    int?    max_reservations = null,  // hard cap; defaults to ceil(max_clients * 1.5) when omitted
    string? masquerade_url = null,
    string? auth_password  = null);   // node shared secret (X-API-PASSWORD)
