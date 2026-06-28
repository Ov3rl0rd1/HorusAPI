namespace HorusAPI.Models;

public record ServerListItem(
    int    id,
    string name,
    string country,
    string city,
    string host,
    string protocol,
    int    current_load,
    int    max_clients,
    bool   is_active,
    string obfs_type,
    string obfs_password,
    string hop);
