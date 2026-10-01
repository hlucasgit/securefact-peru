using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Billing.Contracts;
using SecureFact.Billing.Domain;
using SecureFact.Billing.Infrastructure;
using SecureFact.Organizations.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;
using SecureFact.TaxEngine.Contracts;

namespace SecureFact.Billing.Application;

internal sealed partial class DocumentService(
    BillingDbContext db,
    IDataScope scope,
    ICompanyAdministration companies,
    ITaxCalculator calculator,
    TimeProvider clock,
    IAuditTrail audit) : IDocumentService
{
    private const int MaxLines = 1000;
    private const int MaxPage = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Error DocumentMissing = Error.NotFound(ErrorCodes.DocumentNotFound, "Documento no encontrado", "El documento no existe o no es visible para este contexto.");

    [GeneratedRegex("^[A-Za-z0-9._:-]{8,100}$")]
    private static partial Regex SafeKey();

    public async Task<Result<DocumentDto>> CreateAsync(string idempotencyKey, CreateDocumentRequest request, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Tenant || scope.Current is not { } tenant)
        {
            return Error.Forbidden(ErrorCodes.TenantNotResolved, "Tenant requerido", "Esta operación requiere un contexto de tenant.");
        }

        if (idempotencyKey is null || !SafeKey().IsMatch(idempotencyKey))
        {
            return Error.Validation(ErrorCodes.InvalidRequest, "Idempotency-Key inválida", "Envíe el encabezado Idempotency-Key con 8 a 100 caracteres alfanuméricos, '.', '_', ':' o '-'.");
        }

        if (request is null || request.Lines is not { Count: > 0 and <= MaxLines } || request.Buyer is null)
        {
            return Error.Validation(ErrorCodes.InvalidDocument, "Documento inválido", $"El documento requiere adquirente y entre 1 y {MaxLines} líneas.");
        }

        var requestJson = JsonSerializer.Serialize(request, Json);
        var requestHash = SHA256.HashData(Encoding.UTF8.GetBytes(requestJson));

        var series = await db.Series.AsNoTracking().SingleOrDefaultAsync(s => s.Id == request.SeriesId, cancellationToken);
        if (series is null)
        {
            return Error.NotFound(ErrorCodes.SeriesNotFound, "Serie no encontrada", "La serie no existe o no es visible para este contexto.");
        }

        // A repeated request must return the original result even if the series was deactivated meanwhile.
        var replay = await TryReplayAsync(tenant.Value, idempotencyKey, requestHash, cancellationToken);
        if (replay is not null)
        {
            return replay.Value;
        }

        var validation = await ValidateAsync(series, request, cancellationToken);
        if (validation is not null)
        {
            return validation;
        }

        var calculated = calculator.Calculate(new TaxCalculationRequest(request.Lines.Select(l => l.Tax).ToList(), request.Rates, request.Adjustments));
        if (!calculated.IsSuccess)
        {
            return calculated.Error;
        }

        var totals = calculated.Value;
        var documentId = Guid.CreateVersion7();
        var now = clock.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The idempotency row goes in first: a concurrent request with the same key blocks on the unique index until this
        // transaction ends, then either replays our result (commit) or takes over (rollback).
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO billing.idempotency_key (id, tenant_id, key, request_hash, document_id, created_at)
            VALUES ({Guid.CreateVersion7()}, {tenant.Value}, {idempotencyKey}, {requestHash}, {documentId}, {now})
            ON CONFLICT (tenant_id, key) DO NOTHING
            """, cancellationToken);
        if (inserted == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (await TryReplayAsync(tenant.Value, idempotencyKey, requestHash, cancellationToken))
                ?? Error.Conflict(ErrorCodes.IdempotencyConflict, "Solicitud en conflicto", "No se pudo resolver la clave de idempotencia; reintente.");
        }

        // Atomic, gap-free allocation: the increment commits or rolls back together with the document that uses the number.
        var numbers = await db.Database.SqlQuery<long>($"""
            UPDATE billing.series SET last_number = last_number + 1, updated_at = {now}
            WHERE id = {series.Id} AND is_active AND last_number < {Series.MaxNumber}
            RETURNING last_number AS "Value"
            """).ToListAsync(cancellationToken);
        if (numbers.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Error.Validation(ErrorCodes.SeriesExhausted, "Serie agotada o inactiva", "La serie no puede emitir más números.");
        }

        var document = Document.Create(
            documentId, tenant.Value, series.CompanyId, series.Id, series.DocumentTypeCode, series.Code, numbers[0], request.IssueDate,
            request.Currency, request.Buyer with { DocumentTypeCode = request.Buyer.DocumentTypeCode.Trim(), DocumentNumber = request.Buyer.DocumentNumber.Trim(), Name = request.Buyer.Name.Trim() },
            totals.PayableAmount, JsonSerializer.Serialize(totals, Json), requestJson, requestHash, now);

        for (var i = 0; i < request.Lines.Count; i++)
        {
            var input = request.Lines[i];
            var line = totals.Lines[i];
            document.AddLine(DocumentLine.Create(
                tenant.Value, documentId, line.LineNumber, input.Description.Trim(), input.UnitCode.Trim(), input.ProductCode?.Trim(),
                input.Tax.Quantity, input.Tax.UnitValue, input.Tax.IgvAffectationCode, line.LineExtensionAmount, line.TaxCode,
                line.TotalTaxAmount, line.UnitPriceIncludingTaxes));
        }

        db.Documents.Add(document);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await audit.RecordAsync(new AuditEvent(
            AuditActions.DocumentCreated, "document", documentId.ToString("D"), tenant.Value,
            NewValues: new Dictionary<string, object?>
            {
                ["number"] = $"{document.SeriesCode}-{document.Number}",
                ["type"] = document.DocumentTypeCode,
                ["payable"] = document.PayableAmount,
                ["currency"] = document.Currency,
            }), cancellationToken);

        return ToDto(document);
    }

    public async Task<Result<DocumentDto>> GetAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await db.Documents.AsNoTracking().Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        return document is null ? DocumentMissing : ToDto(document);
    }

    public async Task<IReadOnlyList<DocumentDto>> ListAsync(Guid? companyId, int skip, int take, CancellationToken cancellationToken)
    {
        var query = db.Documents.AsNoTracking().Include(d => d.Lines).AsQueryable();
        if (companyId is { } id)
        {
            query = query.Where(d => d.CompanyId == id);
        }

        var rows = await query
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Number)
            .Skip(Math.Max(skip, 0)).Take(Math.Clamp(take, 1, MaxPage))
            .ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    private async Task<Result<DocumentDto>?> TryReplayAsync(Guid tenantId, string key, byte[] requestHash, CancellationToken cancellationToken)
    {
        var existing = await db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Key == key, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(existing.RequestHash, requestHash))
        {
            return Error.Conflict(ErrorCodes.IdempotencyConflict, "Clave de idempotencia reutilizada", "La clave ya se usó con un contenido distinto.");
        }

        return await GetAsync(existing.DocumentId, cancellationToken);
    }

    private async Task<Error?> ValidateAsync(Series series, CreateDocumentRequest request, CancellationToken cancellationToken)
    {
        if (series.DocumentTypeCode is not (DocumentTypes.Invoice or DocumentTypes.Receipt))
        {
            return Error.Validation(ErrorCodes.DocumentTypeNotSupported, "Tipo de documento no soportado", "Por ahora solo se emiten facturas y boletas; las notas requieren el flujo de CDR (Fase 4).");
        }

        if (!series.IsActive)
        {
            return Error.Validation(ErrorCodes.SeriesInactive, "Serie inactiva", "La serie está desactivada.");
        }

        if (BillingRules.ValidateCurrency(request.Currency) is { } currency)
        {
            return currency;
        }

        if (BillingRules.ValidateBuyer(series.DocumentTypeCode, request.Buyer) is { } buyer)
        {
            return buyer;
        }

        var company = await companies.GetAsync(series.CompanyId, cancellationToken);
        if (!company.IsSuccess)
        {
            return company.Error;
        }

        if (company.Value.Status != CompanyStatus.Active)
        {
            return Error.Validation(ErrorCodes.InvalidDocument, "Empresa inactiva", "La empresa emisora está inactiva.");
        }

        // The tax date is the date in the company's own time zone, not the UTC date (§85/§86).
        var zone = TimeZoneInfo.FindSystemTimeZoneById(company.Value.TimeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
        if (request.IssueDate > today)
        {
            return Error.Validation(ErrorCodes.InvalidDocument, "Fecha de emisión inválida", "La fecha de emisión no puede ser futura.");
        }

        if (request.IssueDate < today.AddDays(-BillingRules.MaxIssueDateAgeDays))
        {
            return Error.Validation(ErrorCodes.InvalidDocument, "Fecha de emisión fuera de plazo", $"La fecha de emisión no puede tener más de {BillingRules.MaxIssueDateAgeDays} días de antigüedad.");
        }

        foreach (var (line, index) in request.Lines.Select((l, i) => (l, i)))
        {
            if (line is null || string.IsNullOrWhiteSpace(line.Description) || line.Description.Trim().Length > 500
                || string.IsNullOrWhiteSpace(line.UnitCode) || line.UnitCode.Trim().Length > 3 || line.Tax is null
                || line.ProductCode is { Length: > 50 })
            {
                return Error.Validation(ErrorCodes.InvalidDocument, $"Línea {index + 1} inválida", "Cada línea requiere descripción (máx. 500), unidad (máx. 3) y datos de impuestos.");
            }
        }

        return null;
    }

    private static DocumentDto ToDto(Document d)
    {
        var totals = JsonSerializer.Deserialize<TaxCalculationResult>(d.TotalsJson, Json)!;
        var lines = d.Lines.OrderBy(l => l.LineNumber).Select(l => new DocumentLineDto(
            l.LineNumber, l.Description, l.UnitCode, l.ProductCode, l.Quantity, l.LineExtensionAmount, l.TaxCode, l.TotalTaxAmount, l.UnitPriceIncludingTaxes)).ToList();
        var buyer = new BuyerSnapshot(d.BuyerDocumentTypeCode, d.BuyerDocumentNumber, d.BuyerName, d.BuyerAddress, d.BuyerEmail);
        return new DocumentDto(d.Id, d.TenantId, d.CompanyId, d.DocumentTypeCode, d.SeriesCode, d.Number, d.IssueDate, d.Currency, buyer, d.Status, lines, totals, d.CreatedAt);
    }
}
