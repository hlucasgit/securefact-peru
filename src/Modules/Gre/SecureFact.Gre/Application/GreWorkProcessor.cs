using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SecureFact.Gre.Contracts;
using SecureFact.Gre.Infrastructure;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Domain;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Gre.Application;

/// <summary>
/// Moves the guides that wait for SUNAT: asks for the answer of the ones with a ticket and sends again the ones whose send failed for a reason that sending again can fix. A guide that the person
/// prepared and did not send has no date to try again and is left alone.
/// </summary>
internal sealed partial class GreWorkProcessor(IServiceScopeFactory scopes, TimeProvider clock, ILogger<GreWorkProcessor> logger) : IGreWorkProcessor
{
    private const int BatchSize = 50;

    private sealed record Due(Guid TenantId, Guid Id, GreState State);

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var due = await DiscoverAsync(clock.GetUtcNow(), cancellationToken);
        var changed = 0;
        foreach (var item in due)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<DataScope>().UseTenant(new TenantId(item.TenantId));
                var service = scope.ServiceProvider.GetRequiredService<IGreService>();
                var result = item.State == GreState.Pending ? await service.RefreshAsync(item.Id, cancellationToken) : await service.SubmitAsync(item.Id, cancellationToken);
                if (!result.IsSuccess)
                {
                    LogSkipped(logger, item.State, result.Error.Code);
                }
                else if (result.Value.State != item.State)
                {
                    changed++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One bad guide must not stop the rest of the pass. The exception goes to the log (never guide or credential data).
                LogFailed(logger, exception);
            }
        }

        return changed;
    }

    private async Task<IReadOnlyList<Due>> DiscoverAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var data = scope.ServiceProvider.GetRequiredService<DataScope>();
        data.UsePlatform("gre worker: inactive tenants");
        var inactive = scope.ServiceProvider.GetService<ITenantStatusReader>() is { } reader ? await reader.ListInactiveAsync(cancellationToken) : [];

        data.UsePlatform("gre worker: discover due guides");
        var db = scope.ServiceProvider.GetRequiredService<GreDbContext>();
        return await db.Guides.AsNoTracking()
            .Where(g => (g.State == GreState.Pending || g.State == GreState.Prepared) && g.NextAttemptAt != null && g.NextAttemptAt <= now && !inactive.Contains(g.TenantId))
            .OrderBy(g => g.NextAttemptAt)
            .Select(g => new Due(g.TenantId, g.Id, g.State))
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "GRE worker left a {State} guide for a later pass: {Code}.")]
    private static partial void LogSkipped(ILogger logger, GreState state, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "GRE worker item failed unexpectedly.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
