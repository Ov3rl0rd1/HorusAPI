using Microsoft.EntityFrameworkCore;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Server> Servers => Set<Server>();
}

public record User(int id, string api_key, string login, string email, string password);
public record Subscription(string id, string transactions_ids, int lengths, DateTime start_date);
public record Server(int id, string domain, string location, int max_clients, int current_clients);
