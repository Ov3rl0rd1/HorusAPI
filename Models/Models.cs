namespace HorusAPI.Models;

public record LoginRequest(string username, string password, string session);
public record LoginResponse(string token, DateTime expiresAt, string username, string session);
public record RegisterRequest(string username, string password, string email);
public record LogoutOthersRequest(string session);
public record ConnectResponse(string config);

public class User
{
    public int id;
    public string username = string.Empty;
    public string password_hash = string.Empty;
    public string[]? sessions;
    public string email = string.Empty;
    public bool is_active;
    public bool is_admin;
    public DateTime created_at;
    public DateTime? expires_at;

    public User() { }

    public User(int id, string username, string password_hash,
        object? sessions, string email, bool is_active, bool is_admin,
        DateTime created_at, DateTime? expires_at)
    {
        this.id            = id;
        this.username      = username;
        this.password_hash = password_hash;
        this.sessions      = sessions is Array arr ? arr.Cast<string>().ToArray() : null;
        this.email         = email;
        this.is_active     = is_active;
        this.is_admin      = is_admin;
        this.created_at    = created_at;
        this.expires_at    = expires_at;
    }
}

public record BestServerItem(
    int    id,
    string name,
    string country,
    string city,
    string host,
    string protocol,
    int    current_load,
    int    max_clients);

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

public record ServerAdminItem(
    int     id,
    string  name,
    string  country,
    string  city,
    string  host,
    string  protocol,
    int     current_load,
    int     max_clients,
    bool    is_active,
    string  obfs_type,
    string  obfs_password,
    string  hop,
    string? masquerade_url);

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
    string? masquerade_url);

public record SetSubscriptionRequest(DateTime expires_at);

public record PingResult(int id, string name, bool reachable, int? statusCode, string? error);

public record ConnectData(
    int    serverId,
    string host,
    string protocol,
    string obfs_type,
    string obfs_password,
    string hop,
    string template);

public record ApiError(string Message);