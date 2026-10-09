using SecureFact.Webhooks.Contracts;

namespace SecureFact.Workers;

/// <summary>Sends the webhook deliveries that are due, on a fixed interval until the host stops (ADR-067). A failed pass is logged and the loop goes on.</summary>
internal sealed partial class WebhookWorker(IWebhookDispatcher dispatcher, TimeProvider clock, IConfiguration configuration, ILogger<WebhookWorker> logger) : BackgroundService
{
    private const int DefaultIntervalSeconds = 5;
    private const int MinimumIntervalSeconds = 1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(configuration.GetValue("Webhooks:WorkerIntervalSeconds", DefaultIntervalSeconds), MinimumIntervalSeconds));
        LogStarted(logger, interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval, clock);
        do
        {
            try
            {
                var attempted = await dispatcher.RunOnceAsync(clock.GetUtcNow(), stoppingToken);
                if (attempted > 0)
                {
                    LogPass(logger, attempted);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook worker started; one pass every {Seconds} s.")]
    private static partial void LogStarted(ILogger logger, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook worker pass: {Attempted} delivery attempt(s).")]
    private static partial void LogPass(ILogger logger, int attempted);

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook worker pass failed; it will try again on the next interval.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
