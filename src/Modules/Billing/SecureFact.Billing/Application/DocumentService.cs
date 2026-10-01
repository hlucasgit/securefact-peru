using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Billing.Contracts;
using SecureFact.Billing.Domain;
using SecureFact.Customers.Contracts;
using SecureFact.Billing.Infrastructure;
using SecureFact.Organizations.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.Rules.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;
using SecureFact.TaxEngine.Contracts;

namespace SecureFact.Billing.Application;

internal sealed partial class DocumentService(
    BillingDbContext db,
    IDataScope scope,
    ICompanyAdministration companies,
    IRuleProvider rules,
    ICustomerAdministration customers,
    IVoidStatusProvider voidStatus,
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

        if (request is null || request.Lines is not { Count: > 0 and <= MaxLines })
        {
            return Error.Validation(ErrorCodes.InvalidDocument, "Documento inválido", $"El documento requiere entre 1 y {MaxLines} líneas.");
        }

        if ((request.Buyer is null) == (request.CustomerId is null))
        {
            return Error.Validation(ErrorCodes.InvalidDocument, "Adquirente inválido", "Indique el adquirente en 'buyer' o por 'customerId', pero no ambos.");
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

        var buyer = request.Buyer;
        if (request.CustomerId is { } customerId)
        {
            var customer = await customers.GetAsync(customerId, cancellationToken);
            if (!customer.IsSuccess)
            {
                return customer.Error;
            }

            if (!customer.Value.IsActive)
            {
                return Error.Validation(ErrorCodes.InvalidDocument, "Cliente inactivo", "El cliente referenciado está inactivo.");
            }

            buyer = new BuyerSnapshot(customer.Value.DocumentTypeCode, customer.Value.DocumentNumber, customer.Value.Name, customer.Value.Address, customer.Value.Email);
        }

        var validation = await ValidateAsync(series, request, buyer, cancellationToken);
        if (validation is not null)
        {
            return validation;
        }

        var rates = await ResolveRatesAsync(request.IssueDate, cancellationToken);
        if (!rates.IsSuccess)
        {
            return rates.Error;
        }

        var calculated = calculator.Calculate(new TaxCalculationRequest(request.Lines.Select(l => l.Tax).ToList(), rates.Value, request.Adjustments));
        if (!calculated.IsSuccess)
        {
            return calculated.Error;
        }

        if (ValidateInstallments(series, request.IssueDate, request.Installments, calculated.Value.PayableAmount) is { } badInstallments)
        {
            return badInstallments;
        }

        return await IssueAsync(
            tenant.Value, idempotencyKey, requestJson, requestHash, series, request.IssueDate, request.Currency, buyer!, request.Lines, calculated.Value, null, cancellationToken);
    }

    private async Task<Result<DocumentDto>> IssueAsync(
        Guid tenantId,
        string idempotencyKey,
        string requestJson,
        byte[] requestHash,
        Series series,
        DateOnly issueDate,
        string currency,
        BuyerSnapshot buyer,
        IReadOnlyList<DocumentLineRequest> lines,
        TaxCalculationResult totals,
        NoteInfo? note,
        CancellationToken cancellationToken)
    {
        var documentId = Guid.CreateVersion7();
        var now = clock.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The idempotency row goes in first: a concurrent request with the same key blocks on the unique index until this
        // transaction ends, then either replays our result (commit) or takes over (rollback).
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO billing.idempotency_key (id, tenant_id, key, request_hash, document_id, created_at)
            VALUES ({Guid.CreateVersion7()}, {tenantId}, {idempotencyKey}, {requestHash}, {documentId}, {now})
            ON CONFLICT (tenant_id, key) DO NOTHING
            """, cancellationToken);
        if (inserted == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (await TryReplayAsync(tenantId, idempotencyKey, requestHash, cancellationToken))
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
            documentId, tenantId, series.CompanyId, series.Id, series.DocumentTypeCode, series.Code, numbers[0], issueDate,
            currency, buyer with { DocumentTypeCode = buyer!.DocumentTypeCode.Trim(), DocumentNumber = buyer.DocumentNumber.Trim().ToUpperInvariant(), Name = buyer.Name.Trim() },
            totals.PayableAmount, JsonSerializer.Serialize(totals, Json), requestJson, requestHash, now);

        for (var i = 0; i < lines.Count; i++)
        {
            var input = lines[i];
            var line = totals.Lines[i];
            document.AddLine(DocumentLine.Create(
                tenantId, documentId, line.LineNumber, input.Description.Trim(), input.UnitCode.Trim(), input.ProductCode?.Trim(),
                input.Tax.Quantity, input.Tax.UnitValue, input.Tax.IgvAffectationCode, line.LineExtensionAmount, line.TaxCode,
                line.TotalTaxAmount, line.UnitPriceIncludingTaxes));
        }

        if (note is not null)
        {
            document.MarkAsNote(note.ReferencedDocumentId, note.ReferencedDocumentTypeCode, note.ReferencedSeries, note.ReferencedNumber, note.ReasonCode, note.Reason);
        }

        db.Documents.Add(document);

        // The integration event commits (or rolls back) with the document: it can neither be lost nor announce a document that does not exist.
        db.OutboxMessages.Add(OutboxMessageEntity.Create(
            tenantId, BillingEvents.DocumentIssued,
            JsonSerializer.Serialize(new DocumentIssuedEvent(tenantId, documentId, document.CompanyId, document.DocumentTypeCode, document.SeriesCode, document.Number), Json), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await audit.RecordAsync(new AuditEvent(
            AuditActions.DocumentCreated, "document", documentId.ToString("D"), tenantId,
            NewValues: new Dictionary<string, object?>
            {
                ["number"] = $"{document.SeriesCode}-{document.Number}",
                ["type"] = document.DocumentTypeCode,
                ["payable"] = document.PayableAmount,
                ["currency"] = document.Currency,
            }), cancellationToken);

        return ToDto(document);
    }

    private static readonly HashSet<string> CreditReasons = ["01", "02", "03", "04", "05", "06", "07", "08", "09", "10"];

    private static readonly HashSet<string> DebitReasons = ["01", "02", "03"];

    public async Task<Result<DocumentDto>> CreateNoteAsync(string idempotencyKey, CreateNoteRequest request, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Tenant || scope.Current is not { } tenant)
        {
            return Error.Forbidden(ErrorCodes.TenantNotResolved, "Tenant requerido", "Esta operación requiere un contexto de tenant.");
        }

        if (idempotencyKey is null || !SafeKey().IsMatch(idempotencyKey))
        {
            return Error.Validation(ErrorCodes.InvalidRequest, "Idempotency-Key inválida", "Envíe el encabezado Idempotency-Key con 8 a 100 caracteres alfanuméricos, '.', '_', ':' o '-'.");
        }

        if (request is null || request.Lines is not { Count: > 0 and <= MaxLines })
        {
            return Error.Validation(ErrorCodes.InvalidDocument, "Nota inválida", $"La nota requiere entre 1 y {MaxLines} líneas.");
        }

        var requestJson = JsonSerializer.Serialize(request, Json);
        var requestHash = SHA256.HashData(Encoding.UTF8.GetBytes(requestJson));

        var series = await db.Series.AsNoTracking().SingleOrDefaultAsync(s => s.Id == request.SeriesId, cancellationToken);
        if (series is null)
        {
            return Error.NotFound(ErrorCodes.SeriesNotFound, "Serie no encontrada", "La serie no existe o no es visible para este contexto.");
        }

        var replay = await TryReplayAsync(tenant.Value, idempotencyKey, requestHash, cancellationToken);
        if (replay is not null)
        {
            return replay.Value;
        }

        var referenced = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == request.ReferencedDocumentId, cancellationToken);
        if (referenced is null)
        {
            return DocumentMissing;
        }

        var validation = await ValidateNoteAsync(series, referenced, request, cancellationToken);
        if (validation is not null)
        {
            return validation;
        }

        var rates = await ResolveRatesAsync(request.IssueDate, cancellationToken);
        if (!rates.IsSuccess)
        {
            return rates.Error;
        }

        var calculated = calculator.Calculate(new TaxCalculationRequest(request.Lines.Select(l => l.Tax).ToList(), rates.Value, request.Adjustments));
        if (!calculated.IsSuccess)
        {
            return calculated.Error;
        }

        var original = JsonSerializer.Deserialize<TaxCalculationResult>(referenced.TotalsJson, Json)!;
        if (series.DocumentTypeCode == DocumentTypes.CreditNote && ExceedsOriginal(calculated.Value, original))
        {
            return Error.Validation(ErrorCodes.NoteExceedsOriginal, "Nota por encima del original", "Una nota de crédito no puede superar el importe total ni los valores de venta del documento que modifica.");
        }

        var buyer = new BuyerSnapshot(referenced.BuyerDocumentTypeCode, referenced.BuyerDocumentNumber, referenced.BuyerName, referenced.BuyerAddress, referenced.BuyerEmail);
        var note = new NoteInfo(request.ReasonCode.Trim(), request.Reason.Trim(), referenced.Id, referenced.DocumentTypeCode, referenced.SeriesCode, referenced.Number);
        return await IssueAsync(
            tenant.Value, idempotencyKey, requestJson, requestHash, series, request.IssueDate, referenced.Currency, buyer, request.Lines, calculated.Value, note, cancellationToken);
    }

    /// <summary>Rules 3286 / 4028 (S16 NotaCredito2_0): the note total and each sale-value category may not exceed the original.</summary>
    private static bool ExceedsOriginal(TaxCalculationResult note, TaxCalculationResult original) =>
        note.PayableAmount > original.PayableAmount
        || note.TotalTaxableGravado > original.TotalTaxableGravado
        || note.TotalExempt > original.TotalExempt
        || note.TotalUnaffected > original.TotalUnaffected
        || note.TotalFree > original.TotalFree
        || note.TotalIgv > original.TotalIgv;

    private async Task<Error?> ValidateNoteAsync(Series series, Document referenced, CreateNoteRequest request, CancellationToken cancellationToken)
    {
        static Error Invalid(string title, string detail) => Error.Validation(ErrorCodes.InvalidDocument, title, detail);

        if (series.DocumentTypeCode is not (DocumentTypes.CreditNote or DocumentTypes.DebitNote))
        {
            return Error.Validation(ErrorCodes.DocumentTypeNotSupported, "Serie no válida para notas", "La serie debe ser de notas de crédito (07) o de débito (08).");
        }

        if (!series.IsActive)
        {
            return Error.Validation(ErrorCodes.SeriesInactive, "Serie inactiva", "La serie está desactivada.");
        }

        // The validation-rules sheets of the notes define no line or global discounts/charges (only the invoice and receipt sheets do): a note states net values.
        if ((request.Adjustments is { } adjustments && adjustments != new GlobalAdjustments())
            || request.Lines.Any(l => l?.Tax is { } t && (t.DiscountAffectingBase != 0 || t.ChargeAffectingBase != 0 || t.DiscountNotAffectingBase != 0 || t.ChargeNotAffectingBase != 0)))
        {
            return Invalid("Notas sin descuentos ni cargos", "Una nota no lleva descuentos ni cargos (ni de línea ni globales): indique los valores netos.");
        }

        if (referenced.CompanyId != series.CompanyId)
        {
            return Invalid("Documento de otra empresa", "La nota y el documento que modifica deben ser de la misma empresa.");
        }

        if (referenced.DocumentTypeCode is not (DocumentTypes.Invoice or DocumentTypes.Receipt))
        {
            return Invalid("Documento no modificable", "Una nota solo modifica una factura o una boleta.");
        }

        if (await voidStatus.IsVoidedOrBeingVoidedAsync(referenced.Id, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.ReferencedDocumentVoided, "Documento anulado", "El documento que la nota modifica está anulado o tiene una baja en curso; una nota no puede referirse a un documento dado de baja.");
        }

        // The note series starts with F for notes of invoices and with B for notes of receipts (S16 rule 0151; confirmed against the SUNAT beta).
        var expectedPrefix = referenced.DocumentTypeCode == DocumentTypes.Invoice ? 'F' : 'B';
        if (series.Code[0] != expectedPrefix)
        {
            return Invalid("Serie incompatible", $"Las notas de {(referenced.DocumentTypeCode == DocumentTypes.Invoice ? "facturas" : "boletas")} usan una serie que empieza con {expectedPrefix}.");
        }

        var credit = series.DocumentTypeCode == DocumentTypes.CreditNote;
        if (!(credit ? CreditReasons : DebitReasons).Contains(request.ReasonCode?.Trim() ?? string.Empty))
        {
            return Invalid("Motivo no soportado", credit ? "El motivo de la nota de crédito debe ser un código 01 a 10 del catálogo 09." : "El motivo de la nota de débito debe ser un código 01 a 03 del catálogo 10.");
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > 500 || reason.Any(c => c is '\n' or '\r' or '\t'))
        {
            return Invalid("Sustento inválido", "El sustento es obligatorio (1 a 500 caracteres, sin saltos de línea ni tabulaciones).");
        }

        var company = await companies.GetAsync(series.CompanyId, cancellationToken);
        if (!company.IsSuccess)
        {
            return company.Error;
        }

        if (company.Value.Status != CompanyStatus.Active)
        {
            return Invalid("Empresa inactiva", "La empresa emisora está inactiva.");
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(company.Value.TimeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
        if (request.IssueDate > today)
        {
            return Invalid("Fecha de emisión inválida", "La fecha de emisión no puede ser futura.");
        }

        if (request.IssueDate < referenced.IssueDate)
        {
            return Invalid("Fecha de emisión inválida", "La nota no puede tener una fecha anterior a la del documento que modifica (regla 2885).");
        }

        var maxAge = await rules.ResolveDecimalAsync(RuleCodes.NoteIssueDateMaxAgeDays, series.DocumentTypeCode, request.IssueDate, cancellationToken);
        if (!maxAge.IsSuccess)
        {
            return maxAge.Error;
        }

        if (request.IssueDate < today.AddDays(-(int)maxAge.Value))
        {
            return Invalid("Fecha de emisión fuera de plazo", $"La fecha de emisión no puede tener más de {(int)maxAge.Value} días de antigüedad.");
        }

        return ValidateLines(request.Lines);
    }

    private static Error? ValidateLines(IReadOnlyList<DocumentLineRequest> lines)
    {
        foreach (var (line, index) in lines.Select((l, i) => (l, i)))
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

    public async Task<IReadOnlyList<DocumentDto>> ListIssuedAsync(Guid companyId, string documentTypeCode, DateOnly issueDate, int skip, int take, CancellationToken cancellationToken)
    {
        var rows = await db.Documents.AsNoTracking().Include(d => d.Lines)
            .Where(d => d.CompanyId == companyId && d.DocumentTypeCode == documentTypeCode && d.IssueDate == issueDate)
            .OrderBy(d => d.SeriesCode).ThenBy(d => d.Number)
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

    /// <summary>IGV, IVAP and ICBPER values in force on the issue date. The reduced IGV rate (10.5 %) needs the issuer's registry status and is not offered yet.</summary>
    private async Task<Result<TaxRates>> ResolveRatesAsync(DateOnly issueDate, CancellationToken cancellationToken)
    {
        var igv = await rules.ResolveDecimalAsync(RuleCodes.IgvRate, "rate", issueDate, cancellationToken);
        var ivap = await rules.ResolveDecimalAsync(RuleCodes.IvapRate, "rate", issueDate, cancellationToken);
        var icbper = await rules.ResolveDecimalAsync(RuleCodes.IcbperUnitAmount, "amount", issueDate, cancellationToken);
        if (!igv.IsSuccess)
        {
            return igv.Error;
        }

        if (!ivap.IsSuccess)
        {
            return ivap.Error;
        }

        return icbper.IsSuccess ? new TaxRates(igv.Value, ivap.Value, icbper.Value) : icbper.Error;
    }

    private async Task<Error?> ValidateAsync(Series series, CreateDocumentRequest request, BuyerSnapshot? buyerToValidate, CancellationToken cancellationToken)
    {
        if (series.DocumentTypeCode is not (DocumentTypes.Invoice or DocumentTypes.Receipt))
        {
            return Error.Validation(ErrorCodes.DocumentTypeNotSupported, "Tipo de documento no soportado", "Este endpoint emite facturas y boletas; las notas de crédito y débito se emiten en /api/v1/notes.");
        }

        if (!series.IsActive)
        {
            return Error.Validation(ErrorCodes.SeriesInactive, "Serie inactiva", "La serie está desactivada.");
        }

        if (BillingRules.ValidateCurrency(request.Currency) is { } currency)
        {
            return currency;
        }

        if (BillingRules.ValidateBuyer(series.DocumentTypeCode, buyerToValidate) is { } buyerError)
        {
            return buyerError;
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

        var maxAge = await rules.ResolveDecimalAsync(RuleCodes.IssueDateMaxAgeDays, series.DocumentTypeCode, request.IssueDate, cancellationToken);
        if (!maxAge.IsSuccess)
        {
            return maxAge.Error;
        }

        if (request.IssueDate < today.AddDays(-(int)maxAge.Value))
        {
            return Error.Validation(ErrorCodes.InvalidDocument, "Fecha de emisión fuera de plazo", $"La fecha de emisión no puede tener más de {(int)maxAge.Value} días de antigüedad.");
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

        // Line and global discounts/charges are not columns: they are part of the request exactly as received, which is kept with the document.
        var stored = ReadStoredRequest(d.OriginalRequestJson);
        var lines = d.Lines.OrderBy(l => l.LineNumber).Select(l =>
        {
            var tax = stored?.Lines is { } storedLines && l.LineNumber >= 1 && l.LineNumber <= storedLines.Count ? storedLines[l.LineNumber - 1]?.Tax : null;
            return new DocumentLineDto(
                l.LineNumber, l.Description, l.UnitCode, l.ProductCode, l.Quantity, l.LineExtensionAmount, l.TaxCode, l.TotalTaxAmount, l.UnitPriceIncludingTaxes, l.UnitValue, l.AffectationCode,
                tax?.DiscountAffectingBase ?? 0m, tax?.ChargeAffectingBase ?? 0m, tax?.DiscountNotAffectingBase ?? 0m, tax?.ChargeNotAffectingBase ?? 0m);
        }).ToList();
        var buyer = new BuyerSnapshot(d.BuyerDocumentTypeCode, d.BuyerDocumentNumber, d.BuyerName, d.BuyerAddress, d.BuyerEmail);
        var note = d.ReferencedDocumentId is { } referencedId
            ? new NoteInfo(d.ReasonCode!, d.Reason!, referencedId, d.ReferencedDocumentTypeCode!, d.ReferencedSeries!, d.ReferencedNumber!.Value)
            : null;
        return new DocumentDto(d.Id, d.TenantId, d.CompanyId, d.DocumentTypeCode, d.SeriesCode, d.Number, d.IssueDate, d.Currency, buyer, d.Status, lines, totals, d.CreatedAt, note, stored?.Adjustments,
            stored?.Installments is { Count: > 0 } storedInstallments ? storedInstallments.Where(i => i is not null).Select(i => i!).ToList() : null);
    }

    /// <summary>Highest installment number the sheet allows: the identifier is <c>Cuota</c> followed by three digits (rule 3246).</summary>
    private const int MaxInstallments = 999;

    /// <summary>
    /// Sale on credit (sheet Factura2_0, "Forma de pago al crédito"): only invoices carry a payment form; each installment has an amount (3253, 3266) and a due date after the
    /// issue date (3267); the net pending amount is the sum of the installments (3319) and cannot exceed the payable amount (3265). With no detraction or withholding supported,
    /// the net pending amount is the whole payable amount, so the installments must add up to it.
    /// </summary>
    private static Error? ValidateInstallments(Series series, DateOnly issueDate, IReadOnlyList<Installment>? installments, decimal payable)
    {
        if (installments is null)
        {
            return null;
        }

        static Error Invalid(string detail) => Error.Validation(ErrorCodes.InvalidDocument, "Cuotas inválidas", detail);

        if (series.DocumentTypeCode != DocumentTypes.Invoice)
        {
            return Invalid("Solo las facturas se venden al crédito: las boletas no llevan forma de pago.");
        }

        if (installments.Count is 0 or > MaxInstallments)
        {
            return Invalid($"Una venta al crédito lleva de 1 a {MaxInstallments} cuotas.");
        }

        decimal sum = 0;
        for (var i = 0; i < installments.Count; i++)
        {
            var installment = installments[i];
            if (installment is null || installment.Amount <= 0 || decimal.Round(installment.Amount, 2) != installment.Amount || installment.Amount > payable)
            {
                return Invalid($"La cuota {i + 1} debe tener un monto mayor que cero, con hasta 2 decimales y sin superar el importe total.");
            }

            if (installment.DueDate <= issueDate)
            {
                return Invalid($"La cuota {i + 1} debe vencer después de la fecha de emisión.");
            }

            sum += installment.Amount;
        }

        return sum == payable ? null : Invalid($"Las cuotas suman {sum:0.00} y el importe total es {payable:0.00}: deben coincidir.");
    }

    private sealed record StoredLine(TaxableLine? Tax);

    private sealed record StoredRequest(List<StoredLine?>? Lines, GlobalAdjustments? Adjustments, List<Installment?>? Installments);

    private static StoredRequest? ReadStoredRequest(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<StoredRequest>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
