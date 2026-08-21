using Microsoft.Extensions.Caching.Memory;

namespace HorusAPI.Services.Auth_Handler;

/// <summary>
/// Evicts a user's cached session entries so the next request reloads a fresh
/// <c>User</c> from the DB. Call after anything that changes a user's binding or
/// subscription (reserve / move / release / set-expiry) — otherwise /whoami and
/// /connect would keep serving the stale <c>current_server_id</c> / <c>expires_at</c>.
/// </summary>
public static class SessionCacheOps
{
    public static void EvictSessions(IMemoryCache cache, IEnumerable<string>? sessions)
    {
        if (sessions is null) return;
        foreach (string s in sessions)
            cache.Remove(SessionAuthHandler.SESSION_CACHE_PREFIX + s);
    }
}
