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
    [DapperAot]   // compile-time command/materializer generation + mismatch diagnostics
    public class SessionAuthHandler : AuthenticationHandler<SessionAuthOptions>
    {
        public const string SESSION_CACHE_PREFIX = "session_";

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
            if (!Request.Headers.TryGetValue(ApiConsts.SESSION_HEADER, out var headerValues))
                return AuthenticateResult.NoResult();

            var sessionKey = headerValues.FirstOrDefault();
            if (string.IsNullOrEmpty(sessionKey))
                return AuthenticateResult.Fail($"Empty {ApiConsts.SESSION_HEADER} header.");

            var user = await GetSessionAsync(sessionKey);

            if (user == null)
                return AuthenticateResult.Fail($"Can not find relevant session key for passed {ApiConsts.SESSION_HEADER} value.");

            Context.Items["User"] = user;

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.id.ToString()),
                new Claim(ClaimTypes.Name, user.username),
                new Claim(ApiConsts.SessionKeyClaimType, sessionKey),
                new Claim(ClaimTypes.Role, user.is_admin ? "Admin" : "User")
            };

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return AuthenticateResult.Success(ticket);
        }
                
        private async Task<User?> GetSessionAsync(string sessionKey)
        {
            string cacheKey = SESSION_CACHE_PREFIX + sessionKey;
            
            if (_cache.TryGetValue(cacheKey, out User? cachedSession))
            {
                return cachedSession;
            }
            
            var user = await GetUserFromPostgresAsync(sessionKey);
            
            if (user != null)
            {
                if (user.sessions.Contains(sessionKey) == false)
                    return null;

                MemoryCacheEntryOptions? cacheEntryOptions = null;

                if (user.expires_at.HasValue && user.expires_at > DateTime.UtcNow)
                {
                    cacheEntryOptions = new MemoryCacheEntryOptions
                    {
                        Size = 1,
                        AbsoluteExpiration = user.expires_at,
                        SlidingExpiration = TimeSpan.FromHours(12),
                        Priority = CacheItemPriority.Normal
                    };
                }
                else
                {
                    cacheEntryOptions = new MemoryCacheEntryOptions
                    {
                        Size = 1,
                        AbsoluteExpiration = DateTime.Now+TimeSpan.FromHours(1),
                        Priority = CacheItemPriority.Low
                    };
                }
            
                _cache.Set(cacheKey, user, cacheEntryOptions);
            }
            
            return user;
        }

        private async Task<User?> GetUserFromPostgresAsync(string sessionKey)
        {
            string[] parts = sessionKey.Split('.');
            if (parts.Length != 2)
                return null;

            int uuid = 0;
            if (int.TryParse(parts[0], out uuid) == false)
                return null;

            string key = parts[1];

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var sql = @"
                SELECT * 
                FROM users 
                WHERE id = @UserId AND sessions @> ARRAY[@SessionKey]::varchar(64)[]";

            var user = await conn.QuerySingleOrDefaultAsync<User>(sql, new { UserId = uuid, SessionKey = sessionKey });

            return user;
        }
    }
}
