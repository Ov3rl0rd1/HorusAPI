using HorusAPI.Endpoints;
using HorusAPI.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ── Kestrel: HTTPS only on port 443 ──────────────────────────────────────────
// Certificate is configured via Kestrel:Certificates:Default in appsettings.json
// (override with env vars: Kestrel__Certificates__Default__Path, KeyPath)
builder.WebHost.ConfigureKestrel((ctx, opts) =>
{
    opts.ListenAnyIP(443, lo => lo.UseHttps());
});

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddScoped<IUserService,        UserService>();
builder.Services.AddScoped<IVpnServerService,   VpnServerService>();
builder.Services.AddScoped<IAdminServerService, AdminServerService>();
builder.Services.AddSingleton<IJwtService,      JwtService>();

// Named HttpClient for admin server ping (5-second timeout, low overhead)
builder.Services.AddHttpClient("ping", client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});

// ── Rate limiting ──────────────────────────────────────────────────────────────
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

// ── JWT Authentication ─────────────────────────────────────────────────────────
var jwtSection = builder.Configuration.GetSection("Jwt");
var secret = jwtSection["Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret is not configured.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer              = jwtSection["Issuer"],
            ValidAudience            = jwtSection["Audience"],
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            ClockSkew                = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// ── Build ──────────────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseHsts();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// ── Endpoints ─────────────────────────────────────────────────────────────────
app.MapAuthEndpoints();
app.MapServerEndpoints();
app.MapAdminEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }))
   .AllowAnonymous()
   .WithTags("Health");

app.Run();
