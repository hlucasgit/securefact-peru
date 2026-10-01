using Microsoft.EntityFrameworkCore;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine.Contracts;
using SecureFact.CpeEngine.Domain;
using SecureFact.CpeEngine.Infrastructure;

namespace SecureFact.CpeEngine.Application;

/// <summary>Answers Billing from the electronic documents: the ones SUNAT rejected, and the ones a voiding file that SUNAT accepted covers.</summary>
internal sealed class IneffectiveDocumentsProvider(CpeDbContext db) : IIneffectiveDocumentsProvider
{
    public async Task<IReadOnlySet<Guid>> FindAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        var electronic = await db.ElectronicDocuments.AsNoTracking()
            .Where(e => documentIds.Contains(e.DocumentId))
            .Select(e => new { e.Id, e.DocumentId, e.State })
            .ToListAsync(cancellationToken);

        var voided = (await db.SummaryItems.AsNoTracking()
            .Where(i => i.ReleasedAt == null && i.LineStatus == 3 && electronic.Select(e => e.Id).Contains(i.ElectronicDocumentId))
            .Join(
                db.ElectronicDocuments.AsNoTracking().Where(e => (e.DocumentTypeCode == ElectronicDocument.VoidType || e.DocumentTypeCode == ElectronicDocument.SummaryType) && e.State == EDocumentState.Accepted),
                i => i.SummaryId, e => e.Id, (i, e) => i.ElectronicDocumentId)
            .ToListAsync(cancellationToken)).ToHashSet();

        return electronic.Where(e => e.State == EDocumentState.Rejected || voided.Contains(e.Id)).Select(e => e.DocumentId).ToHashSet();
    }
}
