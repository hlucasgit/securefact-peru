using SecureFact.Subscriptions.Contracts;

namespace SecureFact.Workers;

/// <summary>
/// Runs the collection pass on a fixed interval until the host stops (ADR-062, ADR-064): charges the months that closed and suspends or reactivates the accounts for non-payment. The pass is
/// idempotent, so a short interval costs one query when there is nothing to do. A failed pass is logged and the loop goes on.
/// </summary>
internal sealed partial class SubscriptionWorker(ICollectionProcessor processor, TimeProvider clock, IConfiguration configuration, ILogger<SubscriptionWorker> logger) : BackgroundService
{
    private const int DefaultIntervalSeconds = 3600;
    private const int MinimumIntervalSeconds = 1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(configuration.GetValue("Subscriptions:WorkerIntervalSeconds", DefaultIntervalSeconds), MinimumIntervalSeconds));
        LogStarted(logger, interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval, clock);
        do
        {
            try
            {
                var result = await processor.RunAsync(clock.GetUtcNow(), stoppingToken);
                if (result.ChargesCreated + result.TenantsSuspended + result.TenantsReactivated > 0)
                {
                    LogPass(logger, result.ChargesCreated, result.TenantsSuspended, result.TenantsReactivated);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogPassFailed(logger, exception);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription worker started; one pass every {Seconds} s.")]
    private static partial void LogStarted(ILogger logger, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription worker pass: {Charges} charge(s) created, {Suspended} account(s) suspended, {Reactivated} reactivated.")]
    private static partial void LogPass(ILogger logger, int charges, int suspended, int reactivated);

    [LoggerMessage(Level = LogLevel.Error, Message = "Subscription worker pass failed; it will try again on the next interval.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
