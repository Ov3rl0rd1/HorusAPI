using System.Threading.RateLimiting;

namespace HorusAPI.Services;

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

    public async ValueTask<bool> TryAcquireAsync(string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;

        using RateLimitLease lease = await _limiter.AcquireAsync(Normalize(email), 1, ct);
        return lease.IsAcquired;
    }

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();

    public ValueTask DisposeAsync() => _limiter.DisposeAsync();
}
