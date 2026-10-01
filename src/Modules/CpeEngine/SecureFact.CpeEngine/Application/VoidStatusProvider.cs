using Microsoft.EntityFrameworkCore;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine.Infrastructure;

namespace SecureFact.CpeEngine.Application;

/// <summary>Answers Billing from the void records: an active item of status 3 (accepted, pending or failed file) reserves the document as annulled.</summary>
internal sealed class VoidStatusProvider(CpeDbContext db) : IVoidStatusProvider
{
    public Task<bool> IsVoidedOrBeingVoidedAsync(Guid documentId, CancellationToken cancellationToken) =>
        db.SummaryItems.AsNoTracking()
            .Where(i => i.ReleasedAt == null && i.LineStatus == 3)
            .Join(db.ElectronicDocuments.AsNoTracking().Where(e => e.DocumentId == documentId), i => i.ElectronicDocumentId, e => e.Id, (i, e) => i)
            .AnyAsync(cancellationToken);
}
