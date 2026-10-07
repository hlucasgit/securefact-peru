using SecureFact.Platform.Tenancy;
using SecureFact.Tenancy;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Workers;

/// <summary>
/// Checks the DNS of the resellers' domains on a fixed interval (ADR-051): a pending domain is promoted when its records appear, so nobody has to press a button, and a verified one that
/// stops answering is noticed. A failed pass is logged and the loop goes on.
/// </summary>
internal sealed partial class DomainWorker(IServiceScopeFactory scopes, DomainsOptions options, TimeProvider clock, ILogger<DomainWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(options.WorkerIntervalSeconds, 1));
        LogStarted(logger, interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval, clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                // The domains of all the resellers: an explicit platform scope, with its reason.
                scope.ServiceProvider.GetRequiredService<DataScope>().UsePlatform("domains worker: check the DNS of the domains that are due");
                var checkedCount = await scope.ServiceProvider.GetRequiredService<IDomains>().VerifyDueAsync(stoppingToken);
                if (checkedCount > 0)
                {
                    LogPass(logger, checkedCount);
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Domain worker started; a pass every {Seconds} s.")]
    private static partial void LogStarted(ILogger logger, double seconds);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Domain pass: {Checked} domains checked.")]
    private static partial void LogPass(ILogger logger, int @checked);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "A pass of the domain worker failed.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
