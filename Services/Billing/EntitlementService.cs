using Dapper;
using HorusAPI.Models;
using HorusAPI.Services.Auth_Handler;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace HorusAPI.Services.Billing;

/// <summary>
/// Keeps <c>users.expires_at</c> — the access cache the hot auth path reads — in sync with
/// the <c>subscriptions</c> source of truth. Access = the latest <c>current_period_end</c>
/// across a user's subscriptions that are still live (period in the future and not
/// pending/failed); no such row → NULL → no access (admins bypass via <see cref="AccessPolicy"/>).
/// </summary>
public interface IEntitlementService
{
    /// <summary>Recompute the cache for a user, then evict their cached sessions so the next
    /// request reloads the fresh expiry. Returns the new <c>expires_at</c> (NULL = no access).</summary>
    Task<DateTime?> RecomputeAndEvictAsync(int userId);
}

// Classic Dapper: reads the sessions VARCHAR(64)[] as string[] (unsupported by the AOT
// materializer) and composes into BillingService's explicit transactions.
public class EntitlementService(IConfiguration cfg, IMemoryCache cache) : IEntitlementService
{
    // A subscription grants access while its period is in the future and it is not a
    // never-activated (pending/failed) row. active/comp/past_due/canceled all keep access
    // until current_period_end — refund/revoke sets that to NOW() to cut it immediately.
    private const string RecomputeSql = """
        UPDATE users SET expires_at = (
            SELECT MAX(current_period_end) FROM subscriptions
            WHERE user_id = @u
              AND current_period_end > NOW()
              AND status NOT IN ('pending', 'failed')
        )
        WHERE id = @u
        RETURNING expires_at
        """;

    private NpgsqlConnection Connect() => new(cfg.GetConnectionString("Postgres"));

    /// <summary>DB-only recompute, composable inside an existing transaction. The caller
    /// must evict the user's cached sessions after the transaction commits.</summary>
    public static Task<DateTime?> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, int userId) =>
        conn.ExecuteScalarAsync<DateTime?>(RecomputeSql, new { u = userId }, tx);

    public async Task<DateTime?> RecomputeAndEvictAsync(int userId)
    {
        await using var conn = Connect();
        await conn.OpenAsync();

        DateTime? expiry = await ApplyAsync(conn, null, userId);

        string[]? sessions = (await conn.QuerySingleOrDefaultAsync<User>(
            "SELECT * FROM users WHERE id = @u", new { u = userId }))?.sessions;
        SessionCacheOps.EvictSessions(cache, sessions);

        return expiry;
    }
}
