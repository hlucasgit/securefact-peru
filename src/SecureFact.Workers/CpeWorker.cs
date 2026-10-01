using SecureFact.CpeEngine.Contracts;

namespace SecureFact.Workers;

/// <summary>Runs <see cref="ICpeWorkProcessor"/> on a fixed interval until the host stops. A failed pass is logged and the loop goes on.</summary>
internal sealed partial class CpeWorker(ICpeWorkProcessor processor, TimeProvider clock, IConfiguration configuration, ILogger<CpeWorker> logger) : BackgroundService
{
    private const int DefaultIntervalSeconds = 15;
    private const int MinimumIntervalSeconds = 1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(configuration.GetValue("Cpe:WorkerIntervalSeconds", DefaultIntervalSeconds), MinimumIntervalSeconds));
        LogStarted(logger, interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval, clock);
        do
        {
            try
            {
                var report = await processor.RunOnceAsync(stoppingToken);
                if (report != WorkReport.Empty)
                {
                    LogPass(logger, report.SummariesCreated, report.Sent, report.Polled, report.Skipped, report.Stuck, report.Errors);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "CPE worker started; one pass every {Seconds} s.")]
    private static partial void LogStarted(ILogger logger, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "CPE worker pass: summaries {Summaries}, sent {Sent}, polled {Polled}, skipped {Skipped}, stuck {Stuck}, errors {Errors}.")]
    private static partial void LogPass(ILogger logger, int summaries, int sent, int polled, int skipped, int stuck, int errors);

    [LoggerMessage(Level = LogLevel.Error, Message = "CPE worker pass failed; it will try again on the next interval.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
