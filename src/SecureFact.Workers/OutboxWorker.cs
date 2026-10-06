using SecureFact.SharedKernel.Messaging;

namespace SecureFact.Workers;

/// <summary>
/// Drains the outbox. While there is work it keeps going without pausing; when a pass delivers nothing it waits for the interval. A failed
/// pass is logged and the loop continues. Once an hour it also removes the messages that were delivered more than <c>Outbox:RetentionDays</c> ago (30 by default, at least 1).
/// </summary>
internal sealed partial class OutboxWorker(IOutboxProcessor processor, TimeProvider clock, IConfiguration configuration, ILogger<OutboxWorker> logger) : BackgroundService
{
    private const int DefaultIntervalSeconds = 2;
    private const int DefaultRetentionDays = 30;
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(configuration.GetValue("Outbox:WorkerIntervalSeconds", DefaultIntervalSeconds), 1));
        var retention = TimeSpan.FromDays(Math.Max(configuration.GetValue("Outbox:RetentionDays", DefaultRetentionDays), 1));
        LogStarted(logger, interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval, clock);
        var busy = false;
        var nextPurge = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!busy && !await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }

                var report = await processor.RunOnceAsync(stoppingToken);
                busy = report.Delivered > 0;
                if (report != OutboxReport.Empty)
                {
                    LogPass(logger, report.Delivered, report.Failed, report.Dead);
                }

                if (clock.GetUtcNow() >= nextPurge)
                {
                    nextPurge = clock.GetUtcNow() + PurgeInterval;
                    var removed = await processor.PurgeAsync(retention, stoppingToken);
                    if (removed > 0)
                    {
                        LogPurged(logger, removed, retention.TotalDays);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                busy = false;
                LogPassFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox worker started; one pass every {Seconds} s when idle.")]
    private static partial void LogStarted(ILogger logger, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox pass: delivered {Delivered}, failed {Failed}, dead {Dead}.")]
    private static partial void LogPass(ILogger logger, int delivered, int failed, int dead);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox purge: removed {Removed} message(s) delivered more than {Days} day(s) ago.")]
    private static partial void LogPurged(ILogger logger, int removed, double days);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox pass failed; it will try again on the next interval.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
