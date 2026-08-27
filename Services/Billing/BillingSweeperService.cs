namespace HorusAPI.Services.Billing;

/// <summary>
/// Periodically releases expired checkout holds and fails the payments behind them, so an
/// abandoned checkout gives its slot back. Deliberately lightweight (one short query loop
/// every few minutes) — the server has 1 core / 1 GB, so nothing here spins or holds memory.
/// </summary>
public sealed class BillingSweeperService(
    IServiceScopeFactory scopeFactory,
    IConfiguration cfg,
    ILogger<BillingSweeperService> log) : BackgroundService
{
    private TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, cfg.GetValue<int?>("Payments:SweepMinutes") ?? 5));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small initial delay so startup isn't competing with a sweep.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var billing = scope.ServiceProvider.GetRequiredService<IBillingService>();
                await billing.SweepAsync();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Billing sweep failed");
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
