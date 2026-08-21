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

    private sealed record TargetRow(bool is_active, bool has_capacity);
}
