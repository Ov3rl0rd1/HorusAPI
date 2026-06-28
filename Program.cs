using HorusAPI.Endpoints;
using HorusAPI.Services;
using HorusAPI.Services.Auth_Handler;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Threading.RateLimiting;

class Program
{

    public static void Main(params string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        ConfigureForwarding(builder);

        builder.Services.AddScoped<IUserService, UserService>();
        builder.Services.AddScoped<IVpnServerService, VpnServerService>();
        builder.Services.AddScoped<IAdminServerService, AdminServerService>();

        AddPingClient(builder);

        AddRateLimiting(builder);

        builder.Services.AddMemoryCache(options =>
        {
            options.SizeLimit = 1000;
            options.CompactionPercentage = 0.20;
        });

        AddSessionAuthentication(builder);

        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

        // Build
        var app = builder.Build();

        app.UseForwardedHeaders();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        // Endpoints
        app.MapAuthEndpoints();
        app.MapServerEndpoints();
        app.MapAdminEndpoints();

        app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }))
           .AllowAnonymous()
           .WithTags("Health");

        app.Run();
    }

    private static void AddSessionAuthentication(WebApplicationBuilder? builder)
    {
        builder.Services.AddAuthentication(SessionAuthOptions.SchemeName)
            .AddScheme<SessionAuthOptions, SessionAuthHandler>(SessionAuthOptions.SchemeName, null);

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("AdminOnly", policy =>
            {
                policy.AddAuthenticationSchemes(SessionAuthOptions.SchemeName);
                policy.RequireRole("Admin");
            });
        });
    }

    private static void AddRateLimiting(WebApplicationBuilder? builder)
    {
        builder.Services.AddRateLimiter(options =>
        {
            options.AddFixedWindowLimiter("auth", o =>
            {
                o.PermitLimit = 10;
                o.Window = TimeSpan.FromMinutes(1);
                o.QueueLimit = 0;
                o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });
            options.RejectionStatusCode = 429;
        });
    }

    private static void AddPingClient(WebApplicationBuilder? builder)
    {
        builder.Services.AddHttpClient("ping", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
        });
    }

    private static void ConfigureForwarding(WebApplicationBuilder? builder)
    {
        // Trusted proxy headers (Nginx → app)
        // Required so rate limiting and logging see the real client IP, not Nginx's.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Trust all Docker-internal proxies; Nginx is the only entry point.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
    }
}
