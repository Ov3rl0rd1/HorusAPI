namespace HorusAPI.Models;

public record BestServerItem(
    int    id,
    string name,
    string country,
    string city,
    string host,
    string protocol,
    int    current_load,
    int    max_clients);
