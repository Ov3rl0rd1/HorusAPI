namespace HorusAPI.Models;

public record LoginRequest(string username, string password);

public record LoginResponse(string token, DateTime expiresAt, string username, string api_key);

public record User(
    int     id,
    string  username,
    string  password_hash,
    string  api_key,
    string  email,
    string  is_active,
    DateTime created_at,
    DateTime? expires_at);
public record ServerListItem(
    int    id,
    string name,
    string country,
    string city,
    string host,
    string protocol,
    int    current_load,
    int    max_clients,
    bool   is_active);

public record ConnectData(
    int    serverId,
    string host,
    int    port,
    string protocol);

public record ApiError(string Message);
