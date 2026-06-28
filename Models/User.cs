namespace HorusAPI.Models;

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
