namespace HorusAPI.Models;

public record ServerListItem(
    int    id,
    string name,
    string country,
    string city,
    string host,
    int    current_load,
    int    max_clients,
    bool   is_active);
