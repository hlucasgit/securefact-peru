using SecureFact.SharedKernel.Messaging;

namespace SecureFact.Workers;

/// <summary>
/// Drains the outbox. While there is work it keeps going without pausing; when a pass delivers nothing it waits for the interval. A failed
/// pass is logged and the loop continues.
/// </summary>
internal sealed partial class OutboxWorker(IOutboxProcessor processor, TimeProvider clock, IConfiguration configuration, ILogger<OutboxWorker> logger) : BackgroundService
{
    private const int DefaultIntervalSeconds = 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(configuration.GetValue("Outbox:WorkerIntervalSeconds", DefaultIntervalSeconds), 1));
        LogStarted(logger, interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval, clock);
        var busy = false;
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox pass failed; it will try again on the next interval.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
