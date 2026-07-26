using HorusAPI.Endpoints;
using HorusAPI.Services;
using HorusAPI.Services.Auth_Handler;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

// Recommended Limits by LayerPer-IP Limit (Short Term): Max 3 requests per minute.Why: Stops automated bots from 
// spamming your server from a single machine.

// Per-IP Limit (Long Term): Max 15 requests per hour.Why: Prevents slow-drip brute force attacks that try to avoid 
// detection by waiting a few seconds between requests.

// Per-Account Limit (Target User): Max 3 requests per hour per email address.Why: Crucial for preventing targeted harassment. 
// If a hacker targets victim@example.com, this stops them from sending hundreds of annoying emails to that specific user, 
// even if the hacker switches IPs.

// Global Server Limit (Fallback): Max 500 total requests per hour across your whole site.Why: Protects your outbound email queue from 
// being overwhelmed (which could get your server IP blacklisted for spam) if a major botnet attacks you using thousands of different IPs.

class Program
{

    public static void Main(params string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        ConfigureForwarding(builder);

        builder.Services.AddScoped<IUserService, UserService>();
        builder.Services.AddScoped<IVpnServerService, VpnServerService>();
        builder.Services.AddScoped<IAdminServerService, AdminServerService>();
        builder.Services.AddScoped<INodeService, NodeService>();
        builder.Services.AddScoped<INodeNotifier, NodeNotifier>();

        AddPingClient(builder);

        builder.Services.AddHttpClient("node", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        AddRateLimiting(builder);

        builder.Services.AddMemoryCache(options =>
        {
            options.SizeLimit = 1000;
            options.CompactionPercentage = 0.20;
        });

        AddSessionAuthentication(builder);

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
        app.MapNodeAuthEndpoints();

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
                o.PermitLimit = 30;
                o.Window = TimeSpan.FromMinutes(1);
                o.QueueLimit = 0;
                o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });


            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromSeconds(10),
                        QueueLimit = 5
                    }));
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
