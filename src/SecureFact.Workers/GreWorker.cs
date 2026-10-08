using SecureFact.Gre.Contracts;

namespace SecureFact.Workers;

/// <summary>Runs <see cref="IGreWorkProcessor"/> on a fixed interval until the host stops: asks SUNAT for the answer of the guides with a ticket and sends again the ones whose send failed. A failed pass is logged and the loop goes on.</summary>
internal sealed partial class GreWorker(IGreWorkProcessor processor, TimeProvider clock, IConfiguration configuration, ILogger<GreWorker> logger) : BackgroundService
{
    private const int DefaultIntervalSeconds = 15;
    private const int MinimumIntervalSeconds = 1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(configuration.GetValue("Gre:WorkerIntervalSeconds", DefaultIntervalSeconds), MinimumIntervalSeconds));
        LogStarted(logger, interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval, clock);
        do
        {
            try
            {
                var changed = await processor.RunOnceAsync(stoppingToken);
                if (changed > 0)
                {
                    LogPass(logger, changed);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "GRE worker started; one pass every {Seconds} s.")]
    private static partial void LogStarted(ILogger logger, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "GRE worker pass: {Changed} guide(s) changed state.")]
    private static partial void LogPass(ILogger logger, int changed);

    [LoggerMessage(Level = LogLevel.Error, Message = "GRE worker pass failed; it will try again on the next interval.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
