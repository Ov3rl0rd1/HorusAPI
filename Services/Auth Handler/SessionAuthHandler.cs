using Dapper;
using HorusAPI.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace HorusAPI.Services.Auth_Handler
{
    public class SessionAuthHandler : AuthenticationHandler<SessionAuthOptions>
    {
        private readonly string? _connectionString;
        private readonly IMemoryCache _cache;

        public SessionAuthHandler(
            IOptionsMonitor<SessionAuthOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IConfiguration configuration,
            IMemoryCache cache)
            : base(options, logger, encoder)
        {
            _connectionString = configuration.GetConnectionString("Postgres");
            _cache = cache;
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Session-Key", out var headerValues))
                return AuthenticateResult.Fail("Missing X-Session-Key header.");

            var sessionKey = headerValues.FirstOrDefault();
            if (string.IsNullOrEmpty(sessionKey))
                return AuthenticateResult.Fail("Empty X-Session-Key header.");

            var user = await GetSessionAsync(sessionKey);

            if (user == null)
                return AuthenticateResult.Fail("Can not find X-Session-Key.");

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.id.ToString()),
                new Claim(ClaimTypes.Name, user.username),
                new Claim(ClaimTypes.Role, user.is_admin ? "Admin" : "User")
            };

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return AuthenticateResult.Success(ticket);
        }
                
        private async Task<User?> GetSessionAsync(string sessionKey)
        {
            string cacheKey = $"session_{sessionKey}";
            
            if (_cache.TryGetValue(cacheKey, out User? cachedSession))
            {
                return cachedSession;
            }
            
            var user = await GetUserFromPostgresAsync(sessionKey);
            
            if (user != null && user.expires_at > DateTime.UtcNow && user.sessions.Contains(sessionKey))
            {
                var cacheEntryOptions = new MemoryCacheEntryOptions
                {
                    Size = 1,
                    AbsoluteExpiration = user.expires_at,
                    SlidingExpiration = TimeSpan.FromHours(12),
                    Priority = CacheItemPriority.Normal
                };
            
                _cache.Set(cacheKey, user, cacheEntryOptions);
            }
            
            return user;
        }

        private async Task<User?> GetUserFromPostgresAsync(string sessionKey)
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var sql = @"
                SELECT * 
                FROM users 
                WHERE sessions @> ARRAY[@SessionKey]";

            var user = await conn.QuerySingleOrDefaultAsync<User>(sql, new { SessionKey = sessionKey });

            return user;
        }
    }
}
