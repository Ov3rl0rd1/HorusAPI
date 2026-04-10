using HorusAPI;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

class Program
{
    public static string GenerateApiKey(int bytes = 32)
    {
        var randomBytes = new byte[bytes];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(randomBytes);
        }

        return Convert.ToBase64String(randomBytes);
    }

    public void GetConnectionString()
    { 
        
    }

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString));

        var app = builder.Build();

        app.UseMiddleware<AuthMiddleware>();

        app.UseWhen(context => context.Request.Path.StartsWithSegments(ApiConsts.API_ROUTE), appBuilder =>
        {
            appBuilder.UseMiddleware<AuthMiddleware>();
        });

        app.MapGet(ApiConsts.API_ROUTE, async (AppDbContext db) => await db.Users.ToListAsync());
        app.MapPost(ApiConsts.LOGIN_ROUTE, async (AppDbContext db) => await db.Users.ToListAsync());

        app.Run();
    }
}