using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine.Contracts;
using SecureFact.CpeEngine.Domain;
using SecureFact.CpeEngine.Infrastructure;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;

namespace SecureFact.CpeEngine.Application;

internal sealed partial class CpeWorkProcessor(IServiceScopeFactory scopes, TimeProvider clock, ILogger<CpeWorkProcessor> logger) : ICpeWorkProcessor
{
    private const int BatchSize = 50;
    private const string DefaultTimeZone = "America/Lima";

    private sealed record Due(Guid TenantId, Guid Id);

    private sealed record Closed(Guid TenantId, Guid CompanyId, DateOnly Date);

    public async Task<WorkReport> RunOnceAsync(CancellationToken cancellationToken, Guid? onlyTenant = null)
    {
        var now = clock.GetUtcNow();
        var report = WorkReport.Empty;

        // 1. Summaries of closed days first, so that they can be sent in this same pass.
        foreach (var group in await DiscoverClosedDaysAsync(now, onlyTenant, cancellationToken))
        {
            report = await InTenantAsync(group.TenantId, report, async (sp, current) =>
            {
                var created = await sp.GetRequiredService<ISummaryService>().CreateAsync(group.CompanyId, group.Date, cancellationToken);
                if (created.IsSuccess)
                {
                    return current with { SummariesCreated = current.SummariesCreated + created.Value.Count };
                }

                // "Nothing to summarize" is normal (the receipts are already in a summary); anything else is worth a line in the log.
                if (created.Error.Code != ErrorCodes.CpeNothingToSummarize)
                {
                    LogItemSkipped(logger, "summary", created.Error.Code);
                }

                return current with { Skipped = current.Skipped + 1 };
            }, cancellationToken);
        }

        var work = await DiscoverDueAsync(now, onlyTenant, cancellationToken);
        report = report with { Stuck = work.Stuck };
        if (work.Stuck > 0)
        {
            LogStuck(logger, work.Stuck);
        }

        // 2. Nothing below touches SUNAT unless a channel is configured.
        await using (var probe = scopes.CreateAsyncScope())
        {
            if (probe.ServiceProvider.GetService<ICpeSubmissionChannel>() is null)
            {
                return report;
            }
        }

        foreach (var due in work.Sends)
        {
            report = await InTenantAsync(due.TenantId, report, async (sp, current) =>
            {
                var result = await sp.GetRequiredService<IElectronicDocumentService>().SendAsync(due.Id, cancellationToken);
                return Count(current, result.IsSuccess, result.IsSuccess ? null : result.Error.Code, sent: true);
            }, cancellationToken);
        }

        foreach (var due in work.Polls)
        {
            report = await InTenantAsync(due.TenantId, report, async (sp, current) =>
            {
                var result = await sp.GetRequiredService<IElectronicDocumentService>().PollAsync(due.Id, cancellationToken);
                return Count(current, result.IsSuccess, result.IsSuccess ? null : result.Error.Code, sent: false);
            }, cancellationToken);
        }

        return report;
    }

    private WorkReport Count(WorkReport current, bool success, string? code, bool sent)
    {
        if (success)
        {
            return sent ? current with { Sent = current.Sent + 1 } : current with { Polled = current.Polled + 1 };
        }

        LogItemSkipped(logger, sent ? "send" : "poll", code ?? "unknown");
        return current with { Skipped = current.Skipped + 1 };
    }

    private async Task<WorkReport> InTenantAsync(Guid tenantId, WorkReport current, Func<IServiceProvider, WorkReport, Task<WorkReport>> action, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<DataScope>().UseTenant(new TenantId(tenantId));
            return await action(scope.ServiceProvider, current);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // One bad item must not stop the rest of the pass. The exception goes to the log (never document or credential data).
            LogItemFailed(logger, exception);
            return current with { Errors = current.Errors + 1 };
        }
    }

    private sealed record DueWork(IReadOnlyList<Due> Sends, IReadOnlyList<Due> Polls, int Stuck);

    private async Task<IReadOnlyList<Closed>> DiscoverClosedDaysAsync(DateTimeOffset now, Guid? onlyTenant, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UsePlatform("cpe worker: discover closed days");
        var db = scope.ServiceProvider.GetRequiredService<CpeDbContext>();

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZone)).DateTime);
        const string receipt = DocumentTypes.Receipt;

        // Receipts of closed days still waiting to be reported. Receipts already inside an unsent summary appear here too; creating
        // a summary for them answers "nothing to summarize", which is harmless.
        var closed = await db.ElectronicDocuments.AsNoTracking()
            .Where(e => (onlyTenant == null || e.TenantId == onlyTenant) && e.State == EDocumentState.ReadyToSend && e.IssueDate < today
                && (e.DocumentTypeCode == receipt || ((e.DocumentTypeCode == DocumentTypes.CreditNote || e.DocumentTypeCode == DocumentTypes.DebitNote) && e.ReferenceTypeCode == receipt)))
            .Select(e => new { e.TenantId, e.CompanyId, e.IssueDate })
            .Distinct()
            .OrderBy(e => e.IssueDate)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        return closed.Select(c => new Closed(c.TenantId, c.CompanyId, c.IssueDate)).ToList();
    }

    private async Task<DueWork> DiscoverDueAsync(DateTimeOffset now, Guid? onlyTenant, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UsePlatform("cpe worker: discover due work");
        var db = scope.ServiceProvider.GetRequiredService<CpeDbContext>();
        const string summary = ElectronicDocument.SummaryType;

        var sends = await db.ElectronicDocuments.AsNoTracking()
            .Where(e => (onlyTenant == null || e.TenantId == onlyTenant) && e.State == EDocumentState.ReadyToSend && (e.DocumentTypeCode == DocumentTypes.Invoice || e.DocumentTypeCode == summary || e.DocumentTypeCode == ElectronicDocument.VoidType
                    || ((e.DocumentTypeCode == DocumentTypes.CreditNote || e.DocumentTypeCode == DocumentTypes.DebitNote) && e.ReferenceTypeCode == DocumentTypes.Invoice))
                && (e.NextAttemptAt == null || e.NextAttemptAt <= now))
            .OrderBy(e => e.CreatedAt)
            .Select(e => new Due(e.TenantId, e.Id))
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        // Only the summary or communication that holds the ticket is polled: the receipts it reports wait in the same state without a ticket of their own and follow the outcome of their summary.
        // Without this filter the pass polled them too, and whether that counted as a poll or as a conflict depended on which timestamp was newer.
        var polls = await db.ElectronicDocuments.AsNoTracking()
            .Where(e => (onlyTenant == null || e.TenantId == onlyTenant) && e.State == EDocumentState.AwaitingTicket && e.Ticket != null && e.Ticket != string.Empty
                && (e.NextAttemptAt == null || e.NextAttemptAt <= now))
            .OrderBy(e => e.UpdatedAt)
            .Select(e => new Due(e.TenantId, e.Id))
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var leaseLimit = now - ICpeWorkProcessor.SendingLease;
        var stuck = await db.ElectronicDocuments.AsNoTracking()
            .CountAsync(e => (onlyTenant == null || e.TenantId == onlyTenant) && e.State == EDocumentState.Sending && e.UpdatedAt < leaseLimit, cancellationToken);

        return new DueWork(sends, polls, stuck);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} electronic document(s) have been in Sending longer than the lease; the outcome at SUNAT is unknown and needs an operator (recover).")]
    private static partial void LogStuck(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker left a {Kind} for a later pass: {Code}.")]
    private static partial void LogItemSkipped(ILogger logger, string kind, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Worker item failed unexpectedly.")]
    private static partial void LogItemFailed(ILogger logger, Exception exception);
}
