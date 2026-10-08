using SecureFact.Notifications.Application;

namespace SecureFact.Workers;

/// <summary>
/// Sends the queued e-mails (ADR-054). While there is mail it keeps going without pausing; when a pass sends nothing it waits for the interval. A failed pass is logged and the loop
/// continues. Once an hour it also removes the e-mails that were sent, or that died, more than <c>Email:RetentionDays</c> ago (30 by default, at least 1).
/// </summary>
internal sealed partial class EmailWorker(IServiceScopeFactory scopes, TimeProvider clock, IConfiguration configuration, ILogger<EmailWorker> logger) : BackgroundService
{
    private const int DefaultIntervalSeconds = 5;
    private const int DefaultRetentionDays = 30;
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan ExpiryInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(configuration.GetValue("Email:WorkerIntervalSeconds", DefaultIntervalSeconds), 1));
        var retention = TimeSpan.FromDays(Math.Max(configuration.GetValue("Email:RetentionDays", DefaultRetentionDays), 1));
        LogStarted(logger, interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval, clock);
        var busy = false;
        var nextPurge = clock.GetUtcNow();
        var nextExpiry = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!busy && !await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }

                await using var scope = scopes.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IEmailDispatcher>();
                var report = await dispatcher.RunOnceAsync(stoppingToken);
                busy = report.Sent > 0;
                if (report != EmailDispatchReport.Empty)
                {
                    LogPass(logger, report.Sent, report.Failed, report.Dead);
                }

                if (clock.GetUtcNow() >= nextExpiry)
                {
                    nextExpiry = clock.GetUtcNow() + ExpiryInterval;
                    var told = await scope.ServiceProvider.GetRequiredService<ICertificateExpiryNotices>().RunAsync(stoppingToken);
                    if (told > 0)
                    {
                        LogExpiry(logger, told);
                    }
                }

                if (clock.GetUtcNow() >= nextPurge)
                {
                    nextPurge = clock.GetUtcNow() + PurgeInterval;
                    var removed = await dispatcher.PurgeAsync(retention, stoppingToken);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "E-mail worker started; one pass every {Seconds} s when idle.")]
    private static partial void LogStarted(ILogger logger, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "E-mail pass: sent {Sent}, failed {Failed}, dead {Dead}.")]
    private static partial void LogPass(ILogger logger, int sent, int failed, int dead);

    [LoggerMessage(Level = LogLevel.Information, Message = "E-mail purge: removed {Removed} e-mail(s) sent or dead more than {Days} day(s) ago.")]
    private static partial void LogPurged(ILogger logger, int removed, double days);

    [LoggerMessage(Level = LogLevel.Information, Message = "Certificate expiry pass: {Told} certificate(s) got a notice.")]
    private static partial void LogExpiry(ILogger logger, int told);

    [LoggerMessage(Level = LogLevel.Error, Message = "E-mail pass failed; it will try again on the next interval.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
