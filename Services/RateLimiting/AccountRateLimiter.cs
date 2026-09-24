using System.Threading.RateLimiting;

namespace HorusAPI.Services;

/// <summary>
/// Outcome of asking for a send slot. <c>RetryAfter</c> is how long until the window
/// refills, and is Zero when the slot was granted or when the limiter cannot say.
/// </summary>
public readonly record struct SendPermit(bool Allowed, TimeSpan RetryAfter);

/// <summary>
/// Per-account layer of the mail rate limiting: caps how many messages a single
/// e-mail address can receive per hour, no matter how many IPs ask for them.
/// This is the layer that stops targeted harassment — an attacker who rotates
/// IPs still cannot mail victim@example.com more than a few times an hour.
/// </summary>
public interface IAccountRateLimiter
{
    /// <summary>Consumes one send slot for <paramref name="email"/>; false when the address is over quota.</summary>
    ValueTask<bool> TryAcquireAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Same, but also reports when the address may be mailed again. Callers that show the
    /// user a countdown need this; "try again later" with no number is the difference
    /// between a dead end and a wait.
    /// </summary>
    ValueTask<SendPermit> TryAcquireDetailedAsync(string email, CancellationToken ct = default);
}

public sealed class AccountRateLimiter : IAccountRateLimiter, IAsyncDisposable
{
    private const int PermitsPerHour = 3;

    // PartitionedRateLimiter evicts idle partitions on its own, so this does not
    // grow with the number of addresses ever seen.
    private readonly PartitionedRateLimiter<string> _limiter =
        PartitionedRateLimiter.Create<string, string>(email =>
            RateLimitPartition.GetFixedWindowLimiter(email, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = PermitsPerHour,
                Window      = TimeSpan.FromHours(1),
                QueueLimit  = 0
            }));

    public async ValueTask<bool> TryAcquireAsync(string email, CancellationToken ct = default) =>
        (await TryAcquireDetailedAsync(email, ct)).Allowed;

    public async ValueTask<SendPermit> TryAcquireDetailedAsync(string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return new SendPermit(false, TimeSpan.Zero);

        using RateLimitLease lease = await _limiter.AcquireAsync(Normalize(email), 1, ct);

        // A rejected fixed-window lease carries how long is left in the current window.
        // It is metadata rather than a guarantee, so a miss degrades to "no number"
        // rather than to a wrong one.
        TimeSpan retryAfter = !lease.IsAcquired &&
                              lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan after)
            ? after
            : TimeSpan.Zero;

        return new SendPermit(lease.IsAcquired, retryAfter);
    }

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();

    public ValueTask DisposeAsync() => _limiter.DisposeAsync();
}
