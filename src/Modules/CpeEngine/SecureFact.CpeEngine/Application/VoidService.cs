using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Billing.Contracts;
using SecureFact.Certificates.Contracts;
using SecureFact.CpeEngine.Contracts;
using SecureFact.CpeEngine.Domain;
using SecureFact.CpeEngine.Infrastructure;
using SecureFact.Organizations.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine.Application;

internal sealed class VoidService(
    CpeDbContext db,
    IDataScope scope,
    ICompanyAdministration companies,
    ICertificateProvider certificates,
    IVoidedDocumentsGenerator generator,
    IXmlSigner signer,
    ICpePackager packager,
    IEDocumentStateMachine machine,
    TimeProvider clock,
    IAuditTrail audit) : IVoidService
{
    /// <summary>Rule 2957 (S16, Comunicación de Baja1_0): a document cannot be voided more than 7 days after its issue date.</summary>
    private const int MaxAgeDays = 7;

    private const int CorrelativeRetries = 5;
    private const int MaxItems = 5000;

    public async Task<Result<IReadOnlyList<SummaryDto>>> CreateAsync(CreateVoidRequest request, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Tenant || scope.Current is not { } tenant)
        {
            return Error.Forbidden(ErrorCodes.TenantNotResolved, "Tenant requerido", "Esta operación requiere un contexto de tenant.");
        }

        if (request is null || request.Items is not { Count: > 0 and <= MaxItems } || request.Items.Any(i => i is null))
        {
            return Error.Validation(ErrorCodes.CpeInvalidDocument, "Solicitud de baja inválida", $"Indique entre 1 y {MaxItems} documentos con su motivo.");
        }

        if (request.Items.Select(i => i.DocumentId).Distinct().Count() != request.Items.Count)
        {
            return Error.Validation(ErrorCodes.CpeInvalidDocument, "Documentos repetidos", "Un documento solo puede aparecer una vez en la comunicación (regla 2348).");
        }

        var company = await companies.GetAsync(request.CompanyId, cancellationToken);
        if (!company.IsSuccess)
        {
            return company.Error;
        }

        var today = LocalToday(company.Value.TimeZone);
        var documentIds = request.Items.Select(i => i.DocumentId).ToList();
        var documents = await db.ElectronicDocuments.AsNoTracking().Where(e => documentIds.Contains(e.DocumentId)).ToDictionaryAsync(e => e.DocumentId, cancellationToken);
        var edIds = documents.Values.Select(d => d.Id).ToList();
        var inProgress = (await db.SummaryItems.AsNoTracking().Where(i => edIds.Contains(i.ElectronicDocumentId) && i.ReleasedAt == null)
            .Join(db.ElectronicDocuments.AsNoTracking().Where(e => e.DocumentTypeCode == ElectronicDocument.VoidType), i => i.SummaryId, e => e.Id, (i, e) => i.ElectronicDocumentId)
            .ToListAsync(cancellationToken)).ToHashSet();

        var chosen = new List<(ElectronicDocument Document, string Reason)>();
        foreach (var item in request.Items)
        {
            if (!documents.TryGetValue(item.DocumentId, out var document) || document.CompanyId != request.CompanyId)
            {
                return Error.NotFound(ErrorCodes.CpeNotFound, "Documento electrónico no encontrado", $"El documento {item.DocumentId} no tiene documento electrónico en esta empresa.");
            }

            if (Refuse(document, inProgress, today) is { } refusal)
            {
                return refusal;
            }

            chosen.Add((document, item.Reason));
        }

        var certificate = await certificates.GetActiveSigningCertificateAsync(request.CompanyId, cancellationToken);
        if (!certificate.IsSuccess)
        {
            return certificate.Error;
        }

        var created = new List<SummaryDto>();
        using (certificate.Value)
        {
            foreach (var day in chosen.GroupBy(c => c.Document.IssueDate).OrderBy(g => g.Key))
            {
                foreach (var block in day.OrderBy(c => c.Document.Series).ThenBy(c => c.Document.Number).Chunk(IVoidedDocumentsGenerator.MaxLines))
                {
                    var communication = await CreateBlockAsync(tenant.Value, company.Value, day.Key, today, block, certificate.Value, cancellationToken);
                    if (!communication.IsSuccess)
                    {
                        return communication.Error;
                    }

                    created.Add(communication.Value);
                }
            }
        }

        return created;
    }

    public async Task<Result<SummaryDto>> GetAsync(Guid voidCommunicationId, CancellationToken cancellationToken)
    {
        var communication = await db.ElectronicDocuments.AsNoTracking().SingleOrDefaultAsync(e => e.Id == voidCommunicationId && e.DocumentTypeCode == ElectronicDocument.VoidType, cancellationToken);
        if (communication is null)
        {
            return Error.NotFound(ErrorCodes.CpeNotFound, "Comunicación de baja no encontrada", "La comunicación no existe o no es visible para este contexto.");
        }

        var items = await db.SummaryItems.AsNoTracking().Where(i => i.SummaryId == communication.Id).OrderBy(i => i.LineNumber).Select(i => i.ElectronicDocumentId).ToListAsync(cancellationToken);
        return new SummaryDto(ElectronicDocumentService.ToDto(communication), communication.IssueDate, items);
    }

    private static Error? Refuse(ElectronicDocument document, HashSet<Guid> inProgress, DateOnly today)
    {
        static Error Cannot(string detail) => Error.Validation(ErrorCodes.CpeNotVoidable, "Documento no anulable", detail);

        var isInvoiceNote = document.DocumentTypeCode is DocumentTypes.CreditNote or DocumentTypes.DebitNote && document.ReferenceTypeCode == DocumentTypes.Invoice;
        if (document.DocumentTypeCode != DocumentTypes.Invoice && !isInvoiceNote)
        {
            return Cannot("Solo se dan de baja facturas y notas de facturas; las boletas y sus notas se anulan por resumen diario.");
        }

        if (document.State is not (EDocumentState.Accepted or EDocumentState.AcceptedWithObservations))
        {
            return Cannot($"El documento {document.Series}-{document.Number} está en estado {document.State}: solo se anula un documento que SUNAT ya aceptó (regla 2105).");
        }

        if (inProgress.Contains(document.Id))
        {
            return Cannot($"El documento {document.Series}-{document.Number} ya está en una comunicación de baja (regla 2323).");
        }

        return today.DayNumber - document.IssueDate.DayNumber > MaxAgeDays
            ? Cannot($"El documento {document.Series}-{document.Number} tiene más de {MaxAgeDays} días desde su emisión (regla 2957).")
            : null;
    }

    private async Task<Result<SummaryDto>> CreateBlockAsync(
        Guid tenantId, CompanyDto company, DateOnly referenceDate, DateOnly generationDate,
        (ElectronicDocument Document, string Reason)[] block, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        var lines = block.Select((b, i) => new VoidedLineData(i + 1, b.Document.DocumentTypeCode, b.Document.Series, b.Document.Number, b.Reason)).ToList();

        for (var attempt = 0; attempt < CorrelativeRetries; attempt++)
        {
            var prefix = $"{company.Ruc}-RA-{generationDate.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)}-";
            var correlative = 1 + await db.ElectronicDocuments.CountAsync(e => e.CompanyId == company.Id && e.DocumentTypeCode == ElectronicDocument.VoidType && e.FileBaseName.StartsWith(prefix), cancellationToken);

            var generated = generator.Generate(new VoidedData(company.Ruc, company.LegalName, referenceDate, generationDate, correlative, lines));
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
            var entity = ElectronicDocument.CreateVoidCommunication(
                Guid.CreateVersion7(), tenantId, company.Id, referenceDate, correlative, generated.Value.FileBaseName, signed.Value.Xml, signed.Value.DigestValue, now);
            if (Lifecycle.Transition(db, machine, entity, EDocumentEvent.DocumentSigned, "Comunicación de baja generada y firmada.", now) is { IsSuccess: false } refused)
            {
                return refused.Error;
            }

            db.ElectronicDocuments.Add(entity);
            for (var i = 0; i < block.Length; i++)
            {
                db.SummaryItems.Add(SummaryItem.Create(tenantId, entity.Id, block[i].Document.Id, i + 1, block[i].Reason.Trim()));
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Another communication took the correlative (or one of the documents) first: try again with a fresh correlative.
                db.ChangeTracker.Clear();
                continue;
            }

            await audit.RecordAsync(new AuditEvent(
                AuditActions.VoidCommunicationCreated, "electronic_document", entity.Id.ToString("D"), tenantId,
                NewValues: new Dictionary<string, object?>
                {
                    ["fileBaseName"] = entity.FileBaseName,
                    ["referenceDate"] = referenceDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    ["documents"] = block.Length,
                }), cancellationToken);
            return new SummaryDto(ElectronicDocumentService.ToDto(entity), referenceDate, block.Select(b => b.Document.Id).ToList());
        }

        return Error.Conflict(ErrorCodes.CpeBusy, "Operación concurrente", "No se pudo asignar un correlativo a la comunicación; intente de nuevo.");
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
}
