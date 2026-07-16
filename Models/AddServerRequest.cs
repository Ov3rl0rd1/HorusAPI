namespace HorusAPI.Models;

public record AddServerRequest(
    string  name,
    string  country,
    string  city,
    string  host,
    string  protocol,
    int     max_clients,
    string  obfs_type,
    string  obfs_password,
    string  hop,
    string? masquerade_url,
    string? auth_password = null);   // node shared secret (X-API-PASSWORD); '' if omitted
