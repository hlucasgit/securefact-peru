using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Billing.Contracts;
using SecureFact.Certificates.Contracts;
using SecureFact.CpeEngine.Contracts;
using SecureFact.CpeEngine.Domain;
using SecureFact.CpeEngine.Infrastructure;
using SecureFact.Organizations.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.Rules.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine.Application;

internal sealed class SummaryService(
    CpeDbContext db,
    IDataScope scope,
    IDocumentService billing,
    ICompanyAdministration companies,
    IRuleProvider rules,
    ICertificateProvider certificates,
    IElectronicDocumentService electronicDocuments,
    ISummaryDocumentGenerator generator,
    IXmlSigner signer,
    ICpePackager packager,
    IEDocumentStateMachine machine,
    TimeProvider clock,
    IAuditTrail audit) : ISummaryService
{
    private const int Page = 200;
    private const int MaxReceiptsPerDay = 5000;
    private const int CorrelativeRetries = 5;

    public async Task<Result<IReadOnlyList<SummaryDto>>> CreateAsync(Guid companyId, DateOnly referenceDate, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Tenant || scope.Current is not { } tenant)
        {
            return Error.Forbidden(ErrorCodes.TenantNotResolved, "Tenant requerido", "Esta operación requiere un contexto de tenant.");
        }

        var company = await companies.GetAsync(companyId, cancellationToken);
        if (!company.IsSuccess)
        {
            return company.Error;
        }

        var today = LocalToday(company.Value.TimeZone);
        if (referenceDate > today)
        {
            return Error.Validation(ErrorCodes.CpeInvalidDocument, "Fecha no válida", "No se puede resumir una fecha futura.");
        }

        // Receipts first, then the notes of receipts issued the same day (a note of an invoice is never summarized).
        var receipts = new List<DocumentDto>();
        foreach (var type in new[] { DocumentTypes.Receipt, DocumentTypes.CreditNote, DocumentTypes.DebitNote })
        {
            var count = 0;
            while (count < MaxReceiptsPerDay)
            {
                var page = await billing.ListIssuedAsync(companyId, type, referenceDate, count, Page, cancellationToken);
                receipts.AddRange(type == DocumentTypes.Receipt ? page : page.Where(n => n.Note?.ReferencedDocumentTypeCode == DocumentTypes.Receipt));
                count += page.Count;
                if (page.Count < Page)
                {
                    break;
                }
            }
        }

        if (receipts.Count == 0)
        {
            return Nothing();
        }

        var igv = await rules.ResolveDecimalAsync(RuleCodes.IgvRate, "rate", referenceDate, cancellationToken);
        if (!igv.IsSuccess)
        {
            return igv.Error;
        }

        var ivap = await rules.ResolveDecimalAsync(RuleCodes.IvapRate, "rate", referenceDate, cancellationToken);
        if (!ivap.IsSuccess)
        {
            return ivap.Error;
        }

        var identification = await rules.ResolveDecimalAsync(RuleCodes.ReceiptIdentificationThreshold, "amount", referenceDate, cancellationToken);
        if (!identification.IsSuccess)
        {
            return identification.Error;
        }

        // Every receipt needs its electronic document (and with it a valid certificate) before it can be reported.
        var pending = new List<(DocumentDto Receipt, ElectronicDocument Document)>();
        foreach (var receipt in receipts)
        {
            var prepared = await electronicDocuments.PrepareAsync(receipt.Id, cancellationToken);
            if (!prepared.IsSuccess)
            {
                return prepared.Error;
            }

            pending.Add((receipt, await db.ElectronicDocuments.SingleAsync(e => e.Id == prepared.Value.Id, cancellationToken)));
        }

        var ids = pending.Select(p => p.Document.Id).ToList();
        var reported = (await db.SummaryItems.Where(i => ids.Contains(i.ElectronicDocumentId) && i.ReleasedAt == null && i.LineStatus == 1)
            .Select(i => i.ElectronicDocumentId).ToListAsync(cancellationToken)).ToHashSet();
        var candidates = pending.Where(p => p.Document.State == EDocumentState.ReadyToSend && !reported.Contains(p.Document.Id)).ToList();

        // SUNAT requires the receipt a note modifies to be already informed (rule 2989). The beta does not check it, production does, so a note
        // is summarized only once its receipt has an accepted summary; until then it waits and the worker brings it in a later summary.
        var referencedIds = candidates.Where(c => c.Receipt.Note is not null).Select(c => c.Receipt.Note!.ReferencedDocumentId).Distinct().ToList();
        var informed = referencedIds.Count == 0
            ? []
            : (await db.ElectronicDocuments.AsNoTracking()
                .Where(e => referencedIds.Contains(e.DocumentId) && (e.State == EDocumentState.Accepted || e.State == EDocumentState.AcceptedWithObservations))
                .Select(e => e.DocumentId).ToListAsync(cancellationToken)).ToHashSet();
        candidates = candidates.Where(c => c.Receipt.Note is null || informed.Contains(c.Receipt.Note.ReferencedDocumentId)).ToList();
        if (candidates.Count == 0)
        {
            return Nothing();
        }

        var certificate = await certificates.GetActiveSigningCertificateAsync(companyId, cancellationToken);
        if (!certificate.IsSuccess)
        {
            return certificate.Error;
        }

        var created = new List<SummaryDto>();
        using (certificate.Value)
        {
            foreach (var block in candidates.Chunk(ISummaryDocumentGenerator.MaxLines))
            {
                var summary = await CreateBlockAsync(tenant.Value, company.Value, referenceDate, today, igv.Value, ivap.Value, identification.Value, block, certificate.Value, cancellationToken);
                if (!summary.IsSuccess)
                {
                    return summary.Error;
                }

                created.Add(summary.Value);
            }
        }

        return created;
    }

    public async Task<Result<SummaryDto>> GetAsync(Guid summaryDocumentId, CancellationToken cancellationToken)
    {
        var summary = await db.ElectronicDocuments.AsNoTracking().SingleOrDefaultAsync(e => e.Id == summaryDocumentId && e.DocumentTypeCode == ElectronicDocument.SummaryType, cancellationToken);
        if (summary is null)
        {
            return Error.NotFound(ErrorCodes.CpeNotFound, "Resumen no encontrado", "El resumen no existe o no es visible para este contexto.");
        }

        // The history keeps released items too (a rejected summary stays inspectable); the DTO lists every receipt it carried.
        var items = await db.SummaryItems.AsNoTracking().Where(i => i.SummaryId == summary.Id).OrderBy(i => i.LineNumber).Select(i => i.ElectronicDocumentId).ToListAsync(cancellationToken);
        return new SummaryDto(ElectronicDocumentService.ToDto(summary), summary.IssueDate, items);
    }

    private async Task<Result<SummaryDto>> CreateBlockAsync(
        Guid tenantId,
        CompanyDto company,
        DateOnly referenceDate,
        DateOnly generationDate,
        decimal igvRate,
        decimal ivapRate,
        decimal identificationThreshold,
        (DocumentDto Receipt, ElectronicDocument Document)[] block,
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        var lines = block.Select((b, i) => SummaryLines.From(b.Receipt, i + 1, igvRate, ivapRate)).ToList();

        for (var attempt = 0; attempt < CorrelativeRetries; attempt++)
        {
            var prefix = $"{company.Ruc}-RC-{generationDate.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)}-";
            var correlative = 1 + await db.ElectronicDocuments.CountAsync(e => e.CompanyId == company.Id && e.DocumentTypeCode == ElectronicDocument.SummaryType && e.FileBaseName.StartsWith(prefix), cancellationToken);

            var generated = generator.Generate(new SummaryData(company.Ruc, company.LegalName, referenceDate, generationDate, correlative, lines, identificationThreshold));
            if (!generated.IsSuccess)
            {
                return generated.Error;
            }

            var signed = signer.Sign(generated.Value.Xml, certificate);
            if (!signed.IsSuccess)
            {
                return signed.Error;
            }

            if (packager.Zip(generated.Value.FileBaseName, signed.Value.Xml) is { IsSuccess: false } badPackage)
            {
                return badPackage.Error;
            }

            var now = clock.GetUtcNow();
            var entity = ElectronicDocument.CreateSummary(
                Guid.CreateVersion7(), tenantId, company.Id, referenceDate, correlative, generated.Value.FileBaseName, signed.Value.Xml, signed.Value.DigestValue, now);
            if (Lifecycle.Transition(db, machine, entity, EDocumentEvent.DocumentSigned, "Resumen generado y firmado.", now) is { IsSuccess: false } refused)
            {
                return refused.Error;
            }

            db.ElectronicDocuments.Add(entity);
            for (var i = 0; i < block.Length; i++)
            {
                db.SummaryItems.Add(SummaryItem.Create(tenantId, entity.Id, block[i].Document.Id, i + 1));
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Another summary took the correlative (or one of the receipts) first, as the unique indexes say: try again with a fresh correlative.
                db.ChangeTracker.Clear();
                continue;
            }

            await audit.RecordAsync(new AuditEvent(
                AuditActions.SummaryCreated, "electronic_document", entity.Id.ToString("D"), tenantId,
                NewValues: new Dictionary<string, object?>
                {
                    ["fileBaseName"] = entity.FileBaseName,
                    ["referenceDate"] = referenceDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    ["receipts"] = block.Length,
                }), cancellationToken);
            return new SummaryDto(ElectronicDocumentService.ToDto(entity), referenceDate, block.Select(b => b.Document.Id).ToList());
        }

        return Error.Conflict(ErrorCodes.CpeBusy, "Operación concurrente", "No se pudo asignar un correlativo al resumen; intente de nuevo.");
    }

    private DateOnly LocalToday(string timeZoneId)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("America/Lima");
        }

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
    }

    private static Error Nothing() => Error.NotFound(ErrorCodes.CpeNothingToSummarize, "Nada que resumir", "No hay boletas pendientes de informar para esa fecha.");
}
