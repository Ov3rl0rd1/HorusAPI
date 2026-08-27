using Dapper;
using Npgsql;

namespace HorusAPI.Services;

public enum ReserveStatus { Ok, NoCapacity, NotFound }

/// <param name="serverId">The node the user is bound to after the call.</param>
/// <param name="newlyReserved">True when this call created a binding that did not exist before (caller must provision the node).</param>
/// <param name="previousServerId">The node the user was moved off, if any (caller must de-provision it).</param>
public record ReserveResult(ReserveStatus status, int? serverId, bool newlyReserved, int? previousServerId)
{
    public static readonly ReserveResult NoCapacity = new(ReserveStatus.NoCapacity, null, false, null);
    public static readonly ReserveResult NotFound   = new(ReserveStatus.NotFound, null, false, null);
}

public enum HoldStatus { Ok, NoCapacity }

/// <summary>Outcome of a checkout slot hold.</summary>
/// <param name="serverId">The node whose seat is held (or the user's existing binding).</param>
/// <param name="holdId">The <c>slot_holds</c> row id, or null when the user was already bound (renewal).</param>
/// <param name="alreadyBound">True when the user already held a permanent slot — no hold was taken.</param>
public record HoldResult(HoldStatus status, int? serverId, int? holdId, bool alreadyBound)
{
    public static readonly HoldResult NoCapacity = new(HoldStatus.NoCapacity, null, null, false);
}

/// <param name="serverId">The node the user is bound to after confirmation (null only if no seat could be secured).</param>
/// <param name="newlyBound">True when this call created the binding (caller must provision the node).</param>
public record ConfirmResult(int? serverId, bool newlyBound);

/// <summary>
/// Owns the slot ("reservation") model: a user is bound to exactly one node
/// (<c>users.current_server_id</c>), and each node counts its bound users in
/// <c>vpn_servers.reserved_count</c>. A node is full when <c>reserved_count = max_clients</c>.
/// Every operation is a single transaction with row locks so concurrent purchases /
/// moves can never oversell a node. DB-only — node (de)provisioning + cache eviction
/// are the caller's job, run after the commit.
/// </summary>
public interface IReservationService
{
    /// <summary>Guarantee the user holds a slot; auto-picks the least-loaded node when unbound.</summary>
    Task<ReserveResult> EnsureReservedAsync(int userId);

    /// <summary>Bind/move the user to <paramref name="serverId"/> (or the least-loaded node when null).</summary>
    Task<ReserveResult> SelectAsync(int userId, int? serverId);

    /// <summary>Drop the user's slot entirely; returns the node they were on (for de-provision).</summary>
    Task<int?> ReleaseAsync(int userId);

    /// <summary>Hold a slot for a checkout in progress (charged to <c>reserved_count</c> immediately,
    /// with a TTL). A no-op returning the existing binding when the user is already bound (renewal).
    /// This is what makes "can't buy when full" hold before payment completes.</summary>
    Task<HoldResult> HoldSlotAsync(int userId, TimeSpan ttl);

    /// <summary>Turn a checkout hold into a permanent binding on payment success. Renewals
    /// (already bound, no hold) are a no-op returning the current binding.</summary>
    Task<ConfirmResult> ConfirmHoldAsync(int userId);

    /// <summary>Release a checkout hold (payment failed/abandoned); frees the held seat.</summary>
    Task ReleaseHoldAsync(int userId);

    /// <summary>Release every hold whose TTL has passed; returns how many were freed. Run periodically.</summary>
    Task<int> SweepExpiredHoldsAsync();
}

// Classic Dapper on purpose (explicit transactions + FOR UPDATE; not [DapperAot]).
public class ReservationService(IConfiguration cfg, ILogger<ReservationService> log) : IReservationService
{
    private NpgsqlConnection Connect() => new(cfg.GetConnectionString("Postgres"));

    // Least-loaded active node that still has a free slot, locked so a parallel
    // reservation can't grab the same last slot. SKIP LOCKED steps over rows another
    // transaction is mid-reserving.
    private const string PickLeastLoaded = """
        SELECT id FROM vpn_servers
        WHERE is_active AND reserved_count < max_clients
        ORDER BY reserved_count ASC, id ASC
        LIMIT 1
        FOR UPDATE SKIP LOCKED
        """;

    public async Task<ReserveResult> EnsureReservedAsync(int userId)
    {
        await using var conn = Connect();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        int? cur = await conn.ExecuteScalarAsync<int?>(
            "SELECT current_server_id FROM users WHERE id = @u FOR UPDATE", new { u = userId }, tx);

        if (cur.HasValue)
        {
            await tx.CommitAsync();
            return new ReserveResult(ReserveStatus.Ok, cur, false, null);
        }

        int? pick = await conn.ExecuteScalarAsync<int?>(PickLeastLoaded, transaction: tx);
        if (!pick.HasValue)
        {
            await tx.RollbackAsync();
            log.LogWarning("Reserve failed for user {UserId}: no node has free capacity", userId);
            return ReserveResult.NoCapacity;
        }

        await conn.ExecuteAsync("UPDATE vpn_servers SET reserved_count = reserved_count + 1 WHERE id = @s", new { s = pick }, tx);
        await conn.ExecuteAsync("UPDATE users SET current_server_id = @s WHERE id = @u", new { s = pick, u = userId }, tx);
        await tx.CommitAsync();
        return new ReserveResult(ReserveStatus.Ok, pick, true, null);
    }

    public async Task<ReserveResult> SelectAsync(int userId, int? serverId)
    {
        await using var conn = Connect();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        int? cur = await conn.ExecuteScalarAsync<int?>(
            "SELECT current_server_id FROM users WHERE id = @u FOR UPDATE", new { u = userId }, tx);

        int target;
        if (serverId.HasValue)
        {
            var row = await conn.QuerySingleOrDefaultAsync<TargetRow>(
                "SELECT is_active, (reserved_count < max_clients) AS has_capacity FROM vpn_servers WHERE id = @s FOR UPDATE",
                new { s = serverId.Value }, tx);

            if (row is null || !row.is_active) { await tx.RollbackAsync(); return ReserveResult.NotFound; }
            if (serverId.Value != cur && !row.has_capacity) { await tx.RollbackAsync(); return ReserveResult.NoCapacity; }
            target = serverId.Value;
        }
        else
        {
            int? pick = await conn.ExecuteScalarAsync<int?>(PickLeastLoaded, transaction: tx);
            if (!pick.HasValue)
            {
                // Nothing free: keep the existing binding if there is one, else fail.
                if (cur.HasValue) { await tx.CommitAsync(); return new ReserveResult(ReserveStatus.Ok, cur, false, null); }
                await tx.RollbackAsync();
                return ReserveResult.NoCapacity;
            }
            target = pick.Value;
        }

        if (cur == target)
        {
            await tx.CommitAsync();
            return new ReserveResult(ReserveStatus.Ok, target, false, null);
        }

        if (cur.HasValue)
            await conn.ExecuteAsync("UPDATE vpn_servers SET reserved_count = GREATEST(reserved_count - 1, 0) WHERE id = @s", new { s = cur.Value }, tx);
        await conn.ExecuteAsync("UPDATE vpn_servers SET reserved_count = reserved_count + 1 WHERE id = @s", new { s = target }, tx);
        await conn.ExecuteAsync("UPDATE users SET current_server_id = @s WHERE id = @u", new { s = target, u = userId }, tx);
        await tx.CommitAsync();
        return new ReserveResult(ReserveStatus.Ok, target, cur is null, cur);
    }

    public async Task<int?> ReleaseAsync(int userId)
    {
        await using var conn = Connect();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        int? cur = await conn.ExecuteScalarAsync<int?>(
            "SELECT current_server_id FROM users WHERE id = @u FOR UPDATE", new { u = userId }, tx);

        if (!cur.HasValue) { await tx.CommitAsync(); return null; }

        await conn.ExecuteAsync("UPDATE vpn_servers SET reserved_count = GREATEST(reserved_count - 1, 0) WHERE id = @s", new { s = cur.Value }, tx);
        await conn.ExecuteAsync("UPDATE users SET current_server_id = NULL WHERE id = @u", new { u = userId }, tx);
        await tx.CommitAsync();
        return cur;
    }

    // ── Checkout slot holds (pending reservations) ────────────────────────────────
    // A hold occupies a seat in reserved_count exactly like a binding, so all the
    // capacity queries (candidates, select, pick) stay unchanged. The user isn't bound
    // yet — ConfirmHold turns the hold into a binding, ReleaseHold/Sweep give the seat back.

    public async Task<HoldResult> HoldSlotAsync(int userId, TimeSpan ttl)
    {
        await using var conn = Connect();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        int? cur = await conn.ExecuteScalarAsync<int?>(
            "SELECT current_server_id FROM users WHERE id = @u FOR UPDATE", new { u = userId }, tx);

        // Already bound → renewal, the seat is theirs; no hold needed.
        if (cur.HasValue)
        {
            await tx.CommitAsync();
            return new HoldResult(HoldStatus.Ok, cur, null, true);
        }

        var hold = await conn.QuerySingleOrDefaultAsync<HoldRow>(
            "SELECT id, user_id, server_id, expires_at FROM slot_holds WHERE user_id = @u FOR UPDATE", new { u = userId }, tx);

        if (hold is not null)
        {
            // A still-live hold: reuse it (idempotent checkout).
            if (hold.expires_at > DateTime.UtcNow)
            {
                await tx.CommitAsync();
                return new HoldResult(HoldStatus.Ok, hold.server_id, hold.id, false);
            }
            // Expired hold lingering: free its seat before taking a fresh one.
            await conn.ExecuteAsync("UPDATE vpn_servers SET reserved_count = GREATEST(reserved_count - 1, 0) WHERE id = @s", new { s = hold.server_id }, tx);
            await conn.ExecuteAsync("DELETE FROM slot_holds WHERE id = @id", new { id = hold.id }, tx);
        }

        int? pick = await conn.ExecuteScalarAsync<int?>(PickLeastLoaded, transaction: tx);
        if (!pick.HasValue)
        {
            await tx.RollbackAsync();
            log.LogWarning("Slot hold failed for user {UserId}: no node has free capacity", userId);
            return HoldResult.NoCapacity;
        }

        await conn.ExecuteAsync("UPDATE vpn_servers SET reserved_count = reserved_count + 1 WHERE id = @s", new { s = pick }, tx);
        int holdId = await conn.ExecuteScalarAsync<int>(
            "INSERT INTO slot_holds (user_id, server_id, expires_at) VALUES (@u, @s, @e) RETURNING id",
            new { u = userId, s = pick, e = DateTime.UtcNow + ttl }, tx);

        await tx.CommitAsync();
        return new HoldResult(HoldStatus.Ok, pick, holdId, false);
    }

    public async Task<ConfirmResult> ConfirmHoldAsync(int userId)
    {
        await using var conn = Connect();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        int? cur = await conn.ExecuteScalarAsync<int?>(
            "SELECT current_server_id FROM users WHERE id = @u FOR UPDATE", new { u = userId }, tx);

        var hold = await conn.QuerySingleOrDefaultAsync<HoldRow>(
            "SELECT id, user_id, server_id, expires_at FROM slot_holds WHERE user_id = @u FOR UPDATE", new { u = userId }, tx);

        if (hold is not null)
        {
            // Bound already (renewal that somehow also holds): keep the binding, release the extra seat.
            if (cur.HasValue)
            {
                if (cur.Value != hold.server_id)
                    await conn.ExecuteAsync("UPDATE vpn_servers SET reserved_count = GREATEST(reserved_count - 1, 0) WHERE id = @s", new { s = hold.server_id }, tx);
                await conn.ExecuteAsync("DELETE FROM slot_holds WHERE id = @id", new { id = hold.id }, tx);
                await tx.CommitAsync();
                return new ConfirmResult(cur, false);
            }

            // Unbound: promote the hold to a binding (the seat is already counted).
            await conn.ExecuteAsync("UPDATE users SET current_server_id = @s WHERE id = @u", new { s = hold.server_id, u = userId }, tx);
            await conn.ExecuteAsync("DELETE FROM slot_holds WHERE id = @id", new { id = hold.id }, tx);
            await tx.CommitAsync();
            return new ConfirmResult(hold.server_id, true);
        }

        // No hold. Bound → renewal, nothing to do.
        if (cur.HasValue)
        {
            await tx.CommitAsync();
            return new ConfirmResult(cur, false);
        }

        // No hold and unbound (hold expired before the webhook arrived): grab a seat now if any.
        int? pick = await conn.ExecuteScalarAsync<int?>(PickLeastLoaded, transaction: tx);
        if (!pick.HasValue)
        {
            await tx.CommitAsync();
            log.LogWarning("ConfirmHold for user {UserId}: paid but no free seat; will bind on next connect", userId);
            return new ConfirmResult(null, false);
        }

        await conn.ExecuteAsync("UPDATE vpn_servers SET reserved_count = reserved_count + 1 WHERE id = @s", new { s = pick }, tx);
        await conn.ExecuteAsync("UPDATE users SET current_server_id = @s WHERE id = @u", new { s = pick, u = userId }, tx);
        await tx.CommitAsync();
        return new ConfirmResult(pick, true);
    }

    public async Task ReleaseHoldAsync(int userId)
    {
        await using var conn = Connect();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var hold = await conn.QuerySingleOrDefaultAsync<HoldRow>(
            "SELECT id, user_id, server_id, expires_at FROM slot_holds WHERE user_id = @u FOR UPDATE", new { u = userId }, tx);

        if (hold is null) { await tx.CommitAsync(); return; }

        await conn.ExecuteAsync("UPDATE vpn_servers SET reserved_count = GREATEST(reserved_count - 1, 0) WHERE id = @s", new { s = hold.server_id }, tx);
        await conn.ExecuteAsync("DELETE FROM slot_holds WHERE id = @id", new { id = hold.id }, tx);
        await tx.CommitAsync();
    }

    public async Task<int> SweepExpiredHoldsAsync()
    {
        await using var conn = Connect();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var expired = (await conn.QueryAsync<HoldRow>(
            "SELECT id, user_id, server_id, expires_at FROM slot_holds WHERE expires_at <= NOW() FOR UPDATE SKIP LOCKED",
            transaction: tx)).ToList();

        foreach (var h in expired)
            await conn.ExecuteAsync("UPDATE vpn_servers SET reserved_count = GREATEST(reserved_count - 1, 0) WHERE id = @s", new { s = h.server_id }, tx);

        if (expired.Count > 0)
            await conn.ExecuteAsync("DELETE FROM slot_holds WHERE id = ANY(@ids)", new { ids = expired.Select(h => h.id).ToArray() }, tx);

        await tx.CommitAsync();
        return expired.Count;
    }

    private sealed record TargetRow(bool is_active, bool has_capacity);
    private sealed record HoldRow(int id, int user_id, int server_id, DateTime expires_at);
}
