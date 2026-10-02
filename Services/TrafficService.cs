using System.Globalization;
using Dapper;
using HorusAPI.Models;
using Npgsql;

namespace HorusAPI.Services;

/// <summary>
/// Each user's monthly traffic, kept per USER rather than per node — which is what makes a
/// monthly allowance survive a change of server. xray counts on the node the user is on; that
/// node reports the month-to-date figure here (telemetry, and its answer when a user is
/// removed), and <see cref="NodeNotifier"/> hands the figure to the next node in POST /users,
/// which restores it into xray before the user's first byte there.
///
/// Every figure is an absolute month-to-date count, merged with GREATEST: a counter only grows
/// within a month, so the larger value is the newer one, and a retried, repeated or late report
/// (an old node still holding a user who has since moved) can never add anything twice.
///
/// Classic Dapper, like the other services that are not on the hot request path.
/// </summary>
public interface ITrafficService
{
    /// <summary>The user's figure for the current month (UTC), or null when nothing is recorded.</summary>
    Task<MonthUsage?> CurrentMonthAsync(Guid vpnUuid);

    /// <summary>Merge reports from a node. Unknown users and malformed lines are skipped.</summary>
    Task<int> RecordAsync(IEnumerable<NodeUserUsage> reports, int? serverId);

    /// <summary>A user's recent months, newest first; null when there is no such user.</summary>
    Task<IReadOnlyList<TrafficMonthItem>?> HistoryAsync(string username, int months = 6);
}

public class TrafficService(IConfiguration cfg, ILogger<TrafficService> log) : ITrafficService
{
    private NpgsqlConnection Connect() => new(cfg.GetConnectionString("Postgres"));

    /// <summary>"yyyy-MM" → the first day of that month, or null. The only format either side speaks.</summary>
    public static DateTime? ParseMonth(string? month) =>
        month is { Length: 7 } && DateTime.TryParseExact(month + "-01", "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static string FormatMonth(DateTime month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>Whether a report line is worth storing. Pure, so the rule is tested on its own.</summary>
    public static bool IsValid(NodeUserUsage u) =>
        Guid.TryParse(u.uuid, out _) && ParseMonth(u.month) is not null && u.total_bytes >= 0 && u.olcrtc_bytes >= 0;

    public async Task<MonthUsage?> CurrentMonthAsync(Guid vpnUuid)
    {
        await using var conn = Connect();
        var row = await conn.QuerySingleOrDefaultAsync<MonthRow>("""
            SELECT t.month::timestamp AS month, t.total_bytes, t.olcrtc_bytes
            FROM traffic_usage t JOIN users u ON u.id = t.user_id
            WHERE u.vpn_uuid = @vpnUuid AND t.month = date_trunc('month', NOW() AT TIME ZONE 'UTC')::date
            """, new { vpnUuid });
        return row is null ? null : new MonthUsage(FormatMonth(row.month), row.total_bytes, row.olcrtc_bytes);
    }

    private sealed record MonthRow(DateTime month, long total_bytes, long olcrtc_bytes);

    public async Task<int> RecordAsync(IEnumerable<NodeUserUsage> reports, int? serverId)
    {
        var lines = reports.Where(IsValid).ToList();
        if (lines.Count == 0) return 0;

        await using var conn = Connect();
        await conn.OpenAsync();
        int written = 0;
        foreach (var u in lines)
        {
            // GREATEST per counter: see the class comment. A user unknown here (deleted, or a
            // node's stale record) matches no row in users and inserts nothing.
            written += await conn.ExecuteAsync("""
                INSERT INTO traffic_usage (user_id, month, total_bytes, olcrtc_bytes, server_id, updated_at)
                SELECT id, @month::date, @total, @olcrtc, @serverId, NOW() FROM users WHERE vpn_uuid = @uuid::uuid
                ON CONFLICT (user_id, month) DO UPDATE SET
                    total_bytes  = GREATEST(traffic_usage.total_bytes,  EXCLUDED.total_bytes),
                    olcrtc_bytes = GREATEST(traffic_usage.olcrtc_bytes, EXCLUDED.olcrtc_bytes),
                    server_id    = CASE WHEN EXCLUDED.total_bytes  > traffic_usage.total_bytes
                                          OR EXCLUDED.olcrtc_bytes > traffic_usage.olcrtc_bytes
                                        THEN EXCLUDED.server_id ELSE traffic_usage.server_id END,
                    updated_at   = NOW()
                """, new { uuid = u.uuid, month = ParseMonth(u.month)!.Value, total = u.total_bytes, olcrtc = u.olcrtc_bytes, serverId });
        }

        if (written < lines.Count)
            log.LogDebug("Traffic: {Skipped} report line(s) named no known user", lines.Count - written);
        return written;
    }

    public async Task<IReadOnlyList<TrafficMonthItem>?> HistoryAsync(string username, int months = 6)
    {
        await using var conn = Connect();
        int? userId = await conn.ExecuteScalarAsync<int?>("SELECT id FROM users WHERE username = @username", new { username });
        if (userId is null) return null;

        // Column order = TrafficMonthItem's parameter order.
        return (await conn.QueryAsync<TrafficMonthItem>("""
            SELECT t.month::timestamp AS month, t.total_bytes, t.olcrtc_bytes, s.name AS server_name, t.updated_at
            FROM traffic_usage t LEFT JOIN vpn_servers s ON s.id = t.server_id
            WHERE t.user_id = @userId
            ORDER BY t.month DESC
            LIMIT @months
            """, new { userId, months })).ToList();
    }
}
