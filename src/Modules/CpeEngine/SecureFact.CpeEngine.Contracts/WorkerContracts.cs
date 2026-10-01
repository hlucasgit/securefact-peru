namespace SecureFact.CpeEngine.Contracts;

/// <summary>What one pass of the background worker did. Counts only; no document data.</summary>
/// <param name="SummariesCreated">Daily summaries created for closed days.</param>
/// <param name="Sent">Documents sent to SUNAT (invoices and summaries).</param>
/// <param name="Polled">Tickets queried with <c>getStatus</c>.</param>
/// <param name="Skipped">Items left for a later pass (precondition not met, or another worker holds them).</param>
/// <param name="Stuck">Documents in <see cref="EDocumentState.Sending"/> for longer than the lease: the outcome at SUNAT is unknown and a person must decide.</param>
/// <param name="Errors">Items whose processing threw an unexpected exception.</param>
public sealed record WorkReport(int SummariesCreated, int Sent, int Polled, int Skipped, int Stuck, int Errors)
{
    public static WorkReport Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// One pass of the electronic-document background work, across tenants: create the daily summaries of closed days, send what is due,
/// query pending tickets and report documents stuck while sending. It is safe to run in several instances at once (every document is
/// claimed with a concurrency token) and it never reaches SUNAT unless a channel is configured.
/// </summary>
public interface ICpeWorkProcessor
{
    /// <summary>Documents in <see cref="EDocumentState.Sending"/> longer than this are reported as stuck.</summary>
    static readonly TimeSpan SendingLease = TimeSpan.FromMinutes(15);

    /// <param name="onlyTenant">Limits the pass to one tenant (a support run); null covers every tenant, as the background worker does.</param>
    Task<WorkReport> RunOnceAsync(CancellationToken cancellationToken, Guid? onlyTenant = null);
}
