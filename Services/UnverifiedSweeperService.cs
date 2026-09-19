using HorusAPI.Services;

namespace HorusAPI.Services;

/// <summary>
/// Deletes abandoned unverified accounts, and clears expired pending tickets on the way past.
///
/// It exists because a half-finished registration is not free: it holds a username and an
/// e-mail address against unique indexes, so the person who mistyped their address cannot
/// simply register again with the same name — the account they cannot reach is in the way.
///
/// The dangerous half of this is the DELETE, not the schedule. Every foreign key into users
/// is ON DELETE CASCADE, so one over-eager row here takes subscriptions, payments and grants
/// with it and says nothing. The guards live in
/// <see cref="IAccountService.DeleteStaleUnverifiedAsync"/> where the SQL is, and they are
/// deliberately broader than the invariants require: an unverified account should never have
/// a session or a subscription in the first place, and the query refuses to touch one that
/// somehow does rather than trusting that it cannot happen.
///
/// Set Accounts:UnverifiedTtlHours to 0 to turn the deletion off entirely; ticket
/// housekeeping continues either way.
/// </summary>
public sealed class UnverifiedSweeperService(
    IServiceScopeFactory scopeFactory,
    IConfiguration cfg,
    ILogger<UnverifiedSweeperService> log) : BackgroundService
{
    /// <summary>How long an unconfirmed account is kept. 0 or less disables deletion.</summary>
    private TimeSpan Ttl => TimeSpan.FromHours(cfg.GetValue<int?>("Accounts:UnverifiedTtlHours") ?? 168);

    private TimeSpan Interval =>
        TimeSpan.FromMinutes(Math.Max(5, cfg.GetValue<int?>("Accounts:SweepMinutes") ?? 60));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let start-up finish first; nothing here is urgent to the minute.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        TimeSpan ttl = Ttl;
        if (ttl <= TimeSpan.Zero)
            log.LogInformation("Unverified-account deletion is disabled (Accounts:UnverifiedTtlHours = 0)");

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();

                await accounts.PurgeExpiredTicketsAsync();

                if (ttl > TimeSpan.Zero)
                    await accounts.DeleteStaleUnverifiedAsync(ttl);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Unverified-account sweep failed");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
