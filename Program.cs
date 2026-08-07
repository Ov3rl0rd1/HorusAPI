using HorusAPI.Endpoints;
using HorusAPI.Services;
using HorusAPI.Services.Auth_Handler;
using Microsoft.AspNetCore.HttpOverrides;

// Rate limiting is layered — see Services/RateLimiting/RateLimitPolicies.cs:
//
//   Per-IP (short term)   3 req/min on mail routes  — stops a bot spamming from one machine.
//   Per-IP (long term)   15 req/hour on mail routes — stops slow-drip attempts that pace themselves.
//   Per-account           3 req/hour per e-mail     — stops targeted harassment across rotating IPs
//                                                     (IAccountRateLimiter, applied in the handlers).
//   Global fallback     500 req/hour on mail routes — protects the outbound queue, and with it our
//                                                     sending reputation, against a botnet.
//
// Everything else gets a per-IP baseline plus a per-endpoint policy (login, verify,
// session, admin, node).

// Public so the test project's WebApplicationFactory<Program> can use it as the entry point.
public partial class Program
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
        builder.Services.AddScoped<IAccountService, AccountService>();
        builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();

        AddPingClient(builder);

        builder.Services.AddHttpClient("node", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        builder.Services.AddHorusRateLimiting();

        // OpenAPI: generates a spec file on disk at build time and (Development only)
        // a local Swagger UI. Never served in Production — see OpenApiSetup.
        builder.Services.AddHorusOpenApi();

        builder.Services.AddMemoryCache(options =>
        {
            options.SizeLimit = 1000;
            options.CompactionPercentage = 0.20;
        });

        AddSessionAuthentication(builder);

        // Build
        var app = builder.Build();

        // Order matters: forwarded headers first so the limiter partitions on the
        // real client IP and not on nginx's address.
        app.UseForwardedHeaders();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        // Local-only API docs. Guarded by the environment so a Production container
        // never exposes them; nginx also does not proxy these paths. Grab the raw
        // spec at /openapi/v1.json, or browse the UI at /swagger.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi("/openapi/{documentName}.json");
            app.UseSwaggerUI(o =>
            {
                o.SwaggerEndpoint($"/openapi/{OpenApiSetup.DocumentName}.json", "HorusAPI v1");
                o.DocumentTitle = "HorusAPI — local docs";
            });
        }

        // Endpoints
        app.MapAuthEndpoints();
        app.MapServerEndpoints();
        app.MapAdminEndpoints();
        app.MapNodeAuthEndpoints();
        app.MapWhoAmIEndpoints();

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
