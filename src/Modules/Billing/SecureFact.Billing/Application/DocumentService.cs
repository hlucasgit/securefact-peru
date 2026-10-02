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
    IIneffectiveDocumentsProvider ineffective,
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

        // A detraction without its own account uses the one registered in the company; the issued document keeps the account that was used.
        var effective = request;
        if (request.Detraction is { } named && string.IsNullOrWhiteSpace(named.AccountNumber))
        {
            var company = await companies.GetAsync(series.CompanyId, cancellationToken);
            if (!company.IsSuccess)
            {
                return company.Error;
            }

            effective = request with { Detraction = named with { AccountNumber = company.Value.DetractionAccount } };
        }

        if (ValidateDeductions(series, effective, calculated.Value.PayableAmount, out var deducted) is { } badDeduction)
        {
            return badDeduction;
        }

        if (ValidateInstallments(series, request.IssueDate, request.Installments, request.InitialPayment, deducted, calculated.Value.PayableAmount) is { } badInstallments)
        {
            return badInstallments;
        }

        return await IssueAsync(
            tenant.Value, idempotencyKey, ReferenceEquals(effective, request) ? requestJson : JsonSerializer.Serialize(effective, Json), requestHash, series, request.IssueDate, request.Currency, buyer!, request.Lines,
            calculated.Value, null, cancellationToken);
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

        // Credit notes on the same document are serialised and counted together before a number is taken, so two requests cannot both fit under the original.
        if (note is not null && series.DocumentTypeCode == DocumentTypes.CreditNote)
        {
            var exceeded = await CheckAccumulatedCreditAsync(note.ReferencedDocumentId, totals, cancellationToken);
            if (exceeded is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return exceeded;
            }
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

    private static readonly HashSet<string> CreditReasons = ["01", "02", "03", "04", "05", "06", "07", "08", "09", "10", "11", "12", "13"];

    /// <summary>Catalogue 09 code of the credit note that adjusts the amounts or dates of the installments of a credit invoice.</summary>
    private const string InstallmentAdjustmentReason = "13";

    private static readonly DocumentLineRequest InstallmentAdjustmentLine = new("Ajuste de montos y/o fechas de cuotas", "ZZ", new TaxableLine(1m, 0m, "10"));

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

        if (request is null)
        {
            return Error.Validation(ErrorCodes.InvalidDocument, "Nota inválida", "La solicitud de la nota es obligatoria.");
        }

        // The adjustment of installments (reason 13) sells and returns nothing: it has no lines of its own and the note carries one line worth zero.
        var adjustsInstallments = request.ReasonCode?.Trim() == InstallmentAdjustmentReason;
        if (adjustsInstallments)
        {
            if (request.Lines is { Count: > 0 })
            {
                return Error.Validation(ErrorCodes.InvalidDocument, "Nota inválida", "La nota de ajuste de cuotas (motivo 13) no lleva líneas: solo las nuevas cuotas.");
            }
        }
        else if (request.Lines is not { Count: > 0 and <= MaxLines })
        {
            return Error.Validation(ErrorCodes.InvalidDocument, "Nota inválida", $"La nota requiere entre 1 y {MaxLines} líneas.");
        }

        var requestJson = JsonSerializer.Serialize(request, Json);
        var requestHash = SHA256.HashData(Encoding.UTF8.GetBytes(requestJson));
        if (adjustsInstallments)
        {
            request = request with { Lines = [InstallmentAdjustmentLine] };
        }

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

        var calculated = calculator.Calculate(new TaxCalculationRequest(request.Lines!.Select(l => l.Tax).ToList(), rates.Value, request.Adjustments));
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
        var note = new NoteInfo(request.ReasonCode!.Trim(), request.Reason.Trim(), referenced.Id, referenced.DocumentTypeCode, referenced.SeriesCode, referenced.Number);
        return await IssueAsync(
            tenant.Value, idempotencyKey, requestJson, requestHash, series, request.IssueDate, referenced.Currency, buyer, request.Lines!, calculated.Value, note, cancellationToken);
    }

    /// <summary>
    /// Credit notes accumulate: all the credit notes that still count on a document, this one included, may not credit more than the document itself, in total and in each
    /// sale-value category. SUNAT checks each note against the original only (rules 3286 and 4028 compare one note with the document); this stricter control is the platform's
    /// own, so that a document cannot be credited twice. Notes SUNAT rejected and notes that were voided do not count. Debit notes are not netted against it.
    /// The check runs inside the issuing transaction under an advisory lock on the referenced document.
    /// </summary>
    private async Task<Error?> CheckAccumulatedCreditAsync(Guid referencedId, TaxCalculationResult note, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({referencedId.ToString("D")}, 0))", cancellationToken);

        var referenced = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == referencedId, cancellationToken);
        var priors = await db.Documents.AsNoTracking()
            .Where(d => d.ReferencedDocumentId == referencedId && d.DocumentTypeCode == DocumentTypes.CreditNote)
            .Select(d => new { d.Id, d.TotalsJson })
            .ToListAsync(cancellationToken);
        if (priors.Count == 0)
        {
            return null;
        }

        var dead = await ineffective.FindAsync(priors.Select(p => p.Id).ToList(), cancellationToken);
        var counted = priors.Where(p => !dead.Contains(p.Id)).Select(p => JsonSerializer.Deserialize<TaxCalculationResult>(p.TotalsJson, Json)!).ToList();
        if (counted.Count == 0)
        {
            return null;
        }

        var original = JsonSerializer.Deserialize<TaxCalculationResult>(referenced.TotalsJson, Json)!;
        var credited = counted.Append(note).ToList();
        var exceeds = credited.Sum(n => n.PayableAmount) > original.PayableAmount
            || credited.Sum(n => n.TotalTaxableGravado) > original.TotalTaxableGravado
            || credited.Sum(n => n.TotalExempt) > original.TotalExempt
            || credited.Sum(n => n.TotalUnaffected) > original.TotalUnaffected
            || credited.Sum(n => n.TotalFree) > original.TotalFree
            || credited.Sum(n => n.TotalIgv) > original.TotalIgv
            || credited.Sum(n => n.TotalIvap) > original.TotalIvap
            || credited.Sum(n => n.TotalExport) > original.TotalExport;
        return exceeds
            ? Error.Validation(
                ErrorCodes.NoteExceedsOriginal, "Notas acumuladas por encima del original",
                $"Las notas de crédito vigentes de este documento ({counted.Sum(n => n.PayableAmount):0.00}) más esta ({note.PayableAmount:0.00}) superan su importe total ({original.PayableAmount:0.00}).")
            : null;
    }

    /// <summary>Rules 3286 / 4028 (S16 NotaCredito2_0): the note total and each sale-value category may not exceed the original.</summary>
    private static bool ExceedsOriginal(TaxCalculationResult note, TaxCalculationResult original) =>
        note.PayableAmount > original.PayableAmount
        || note.TotalTaxableGravado > original.TotalTaxableGravado
        || note.TotalExempt > original.TotalExempt
        || note.TotalUnaffected > original.TotalUnaffected
        || note.TotalFree > original.TotalFree
        || note.TotalIgv > original.TotalIgv
        || note.TotalIvap > original.TotalIvap
        || note.TotalExport > original.TotalExport;

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
            || request.Lines!.Any(l => l?.Tax is { } t && (t.DiscountAffectingBase != 0 || t.ChargeAffectingBase != 0 || t.DiscountNotAffectingBase != 0 || t.ChargeNotAffectingBase != 0)))
        {
            return Invalid("Notas sin descuentos ni cargos", "Una nota no lleva descuentos ni cargos (ni de línea ni globales): indique los valores netos.");
        }

        if (request.Lines!.Any(l => l is { Fishing: not null } or { Transport: not null }))
        {
            return Invalid("Notas sin datos de pesca ni de transporte", "Una nota no lleva datos de recursos hidrobiológicos ni de transporte de carga: son de la factura que modifica.");
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
            return Invalid("Motivo no soportado", credit ? "El motivo de la nota de crédito debe ser un código 01 a 13 del catálogo 09." : "El motivo de la nota de débito debe ser un código 01 a 03 del catálogo 10.");
        }

        if (ValidateNoteInstallments(request, referenced) is { } badInstallments)
        {
            return badInstallments;
        }

        if (ValidateIvapAdjustment(request, referenced) is { } badIvap)
        {
            return badIvap;
        }

        if (ValidateExportAdjustment(request, referenced) is { } badExport)
        {
            return badExport;
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

        return ValidateLines(request.Lines!);
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

        if (ValidateOperationType(series, request) is { } operationError)
        {
            return operationError;
        }

        if (ValidateLineDetails(request) is { } detailsError)
        {
            return detailsError;
        }

        if (BillingRules.ValidateBuyer(series.DocumentTypeCode, buyerToValidate, request.OperationTypeCode == OperationTypes.Export) is { } buyerError)
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
            var storedLine = stored?.Lines is { } storedLines && l.LineNumber >= 1 && l.LineNumber <= storedLines.Count ? storedLines[l.LineNumber - 1] : null;
            var tax = storedLine?.Tax;
            return new DocumentLineDto(
                l.LineNumber, l.Description, l.UnitCode, l.ProductCode, l.Quantity, l.LineExtensionAmount, l.TaxCode, l.TotalTaxAmount, l.UnitPriceIncludingTaxes, l.UnitValue, l.AffectationCode,
                tax?.DiscountAffectingBase ?? 0m, tax?.ChargeAffectingBase ?? 0m, tax?.DiscountNotAffectingBase ?? 0m, tax?.ChargeNotAffectingBase ?? 0m, storedLine?.Fishing, storedLine?.Transport);
        }).ToList();
        var buyer = new BuyerSnapshot(d.BuyerDocumentTypeCode, d.BuyerDocumentNumber, d.BuyerName, d.BuyerAddress, d.BuyerEmail);
        var note = d.ReferencedDocumentId is { } referencedId
            ? new NoteInfo(d.ReasonCode!, d.Reason!, referencedId, d.ReferencedDocumentTypeCode!, d.ReferencedSeries!, d.ReferencedNumber!.Value)
            : null;
        return new DocumentDto(d.Id, d.TenantId, d.CompanyId, d.DocumentTypeCode, d.SeriesCode, d.Number, d.IssueDate, d.Currency, buyer, d.Status, lines, totals, d.CreatedAt, note, stored?.Adjustments,
            stored?.Installments is { Count: > 0 } storedInstallments ? storedInstallments.Where(i => i is not null).Select(i => i!).ToList() : null,
            stored?.Detraction is { } storedDetraction ? OperationTypes.ForDetraction(storedDetraction.GoodsOrServiceCode) : stored?.OperationTypeCode ?? OperationTypes.Sale, stored?.InitialPayment, stored?.Detraction,
            stored?.Retention is { } retention ? new IgvRetention(retention.Percentage, totals.PayableAmount, RetainedAmount(totals.PayableAmount, retention.Percentage)) : null);
    }

    /// <summary>Affectation code of the IVAP (catalogue 07).</summary>
    private const string IvapAffectation = "17";

    /// <summary>Catalogue 09 code of the credit note that adjusts operations taxed with the IVAP.</summary>
    private const string IvapAdjustmentReason = "12";

    /// <summary>
    /// Reason 12 (sheet NotaCredito2_0, rules 2644, 3221, 3230): the note adjusts a document that carries the IVAP and has only IVAP lines (affectation 17); the IVAP is
    /// reserved to this reason among notes.
    /// </summary>
    private static Error? ValidateIvapAdjustment(CreateNoteRequest request, Document referenced)
    {
        static Error Invalid(string detail) => Error.Validation(ErrorCodes.InvalidDocument, "Ajuste IVAP inválido", detail);

        var lines = request.Lines ?? [];
        var ivapLines = lines.Count(l => l?.Tax?.IgvAffectationCode == IvapAffectation);
        if (request.ReasonCode?.Trim() != IvapAdjustmentReason)
        {
            return ivapLines > 0 ? Invalid("Las líneas afectas al IVAP (afectación 17) son solo de la nota de crédito de motivo 12.") : null;
        }

        if (ivapLines != lines.Count)
        {
            return Invalid("La nota de motivo 12 (ajustes afectos al IVAP) lleva solo líneas con afectación 17.");
        }

        return JsonSerializer.Deserialize<TaxCalculationResult>(referenced.TotalsJson, Json)!.TotalIvap > 0
            ? null
            : Invalid("El motivo 12 solo modifica un documento afecto al IVAP.");
    }

    /// <summary>
    /// Reason 13 (sheet NotaCredito2_0, rules 3257, 3259, 3260, 3319–3321): it modifies an invoice sold on credit, gives the new installments, each due after the
    /// issue date of the invoice, and their sum (the new net pending amount) may not exceed the invoice total. No other reason carries installments.
    /// </summary>
    private static Error? ValidateNoteInstallments(CreateNoteRequest request, Document referenced)
    {
        static Error Invalid(string detail) => Error.Validation(ErrorCodes.InvalidDocument, "Cuotas inválidas", detail);

        var installments = request.Installments;
        if (request.ReasonCode?.Trim() != InstallmentAdjustmentReason)
        {
            return installments is { Count: > 0 } ? Invalid("Solo la nota de crédito de motivo 13 lleva cuotas.") : null;
        }

        if (referenced.DocumentTypeCode != DocumentTypes.Invoice)
        {
            return Invalid("El motivo 13 (ajuste de cuotas) solo modifica facturas.");
        }

        if (ReadStoredRequest(referenced.OriginalRequestJson)?.Installments is not { Count: > 0 })
        {
            return Invalid("El motivo 13 solo modifica una factura vendida al crédito, y esta se vendió al contado.");
        }

        if (installments is not { Count: > 0 and <= MaxInstallments })
        {
            return Invalid($"El motivo 13 lleva de 1 a {MaxInstallments} cuotas.");
        }

        var total = JsonSerializer.Deserialize<TaxCalculationResult>(referenced.TotalsJson, Json)!.PayableAmount;
        decimal sum = 0;
        for (var i = 0; i < installments.Count; i++)
        {
            var installment = installments[i];
            if (installment is null || installment.Amount <= 0 || decimal.Round(installment.Amount, 2) != installment.Amount)
            {
                return Invalid($"La cuota {i + 1} debe tener un monto mayor que cero, con hasta 2 decimales.");
            }

            if (installment.DueDate <= referenced.IssueDate)
            {
                return Invalid($"La cuota {i + 1} debe vencer después de la fecha de emisión de la factura.");
            }

            sum += installment.Amount;
        }

        return sum <= total ? null : Invalid($"Las cuotas suman {sum:0.00}, más que el importe de la factura ({total:0.00}).");
    }

    /// <summary>Highest installment number the sheet allows: the identifier is <c>Cuota</c> followed by three digits (rule 3246).</summary>
    private const int MaxInstallments = 999;

    /// <summary>
    /// Sale on credit (sheet Factura2_0, "Forma de pago al crédito"): only invoices carry a payment form; each installment has an amount (3253, 3266) and a due date after the
    /// issue date (3267); the net pending amount is the sum of the installments (3319) and cannot exceed the payable amount (3265). With no detraction or withholding supported,
    /// the net pending amount is the whole payable amount, so the installments must add up to it.
    /// </summary>
    private static Error? ValidateInstallments(Series series, DateOnly issueDate, IReadOnlyList<Installment>? installments, decimal? initialPayment, decimal deductions, decimal payable)
    {
        static Error Invalid(string detail) => Error.Validation(ErrorCodes.InvalidDocument, "Cuotas inválidas", detail);

        if (installments is null)
        {
            return initialPayment is null ? null : Invalid("La entrega inicial es de una venta al crédito: indique también las cuotas.");
        }

        // The part paid on the issue date (RS 193-2020, annex 1, fields 49-A and 64-A: a credit sale is paid "total o parcialmente en fecha posterior"; the net pending
        // amount is the pending balance): the installments add up to what is left. Without it they add up to the whole payable amount.
        if (initialPayment is { } initial && (initial <= 0 || decimal.Round(initial, 2) != initial || initial >= payable - deductions))
        {
            return Invalid("La entrega inicial debe ser mayor que cero, tener hasta 2 decimales y ser menor que el importe total.");
        }

        // The net pending amount also excludes the detraction and the withholding the buyer deposits or keeps (RS 193-2020, annex 1, field 64-A).
        var pending = payable - deductions - (initialPayment ?? 0m);

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

        return sum == pending
            ? null
            : Invalid(initialPayment is null
                ? $"Las cuotas suman {sum:0.00} y lo pendiente de pago es {pending:0.00}: deben coincidir."
                : $"Las cuotas suman {sum:0.00} y lo pendiente tras la entrega inicial es {pending:0.00}: deben coincidir.");
    }

    /// <summary>Catalogue 54 codes (goods and services subject to detraction). The fishing (004) and transport (027, 028) codes go with the operation types 1002–1004 (<see cref="OperationTypes.ForDetraction"/>).</summary>
    private static readonly HashSet<string> DetractionCodes =
    [
        "001", "002", "003", "004", "005", "007", "008", "009", "010", "011", "012", "013", "014", "015", "016", "017", "019", "020", "021", "022", "023", "024", "025", "026", "027", "028",
        "030", "031", "032", "034", "035", "036", "037", "038", "039", "040", "041", "042", "043", "044", "045", "046", "047", "099",
    ];

    [GeneratedRegex("^[0-9A-Za-z-]{1,100}$")]
    private static partial Regex AccountNumberPattern();

    /// <summary>
    /// Detraction and withholding of an invoice (sheet Factura2_0: rules 3033–3037, 3127, 3128, 3208, 3262–3264; RS 037-2002, art. 5). SUNAT checks the structure but not the percentage or the
    /// amount of the detraction, which are the issuer's data: the platform checks that they agree (the deposit is rounded to whole soles, so within one sol). A detraction needs an invoice in soles
    /// and its code from catalogue 54; a withholding is a percentage of the payable amount. They exclude each other and neither applies to an export. <paramref name="deducted"/> is the amount
    /// that the net pending amount of a credit sale leaves out.
    /// </summary>
    private static Error? ValidateDeductions(Series series, CreateDocumentRequest request, decimal payable, out decimal deducted)
    {
        static Error Invalid(string detail) => Error.Validation(ErrorCodes.InvalidDocument, "Detracción o retención inválida", detail);

        deducted = 0m;
        var detraction = request.Detraction;
        var retention = request.Retention;
        if (detraction is null && retention is null)
        {
            return null;
        }

        if (series.DocumentTypeCode != DocumentTypes.Invoice)
        {
            return Invalid("La detracción y la retención del IGV son de las facturas.");
        }

        if (request.OperationTypeCode?.Trim() == OperationTypes.Export)
        {
            return Invalid("Una exportación no está sujeta a detracción ni a retención.");
        }

        if (detraction is not null && retention is not null)
        {
            return Invalid("Una operación sujeta a detracción queda fuera de la retención del IGV (RS 037-2002, art. 5): indique una u otra.");
        }

        if (detraction is not null)
        {
            if (request.Currency != "PEN")
            {
                return Invalid("La detracción se declara en soles: la factura debe ser en soles.");
            }

            if (detraction.GoodsOrServiceCode is null || !DetractionCodes.Contains(detraction.GoodsOrServiceCode.Trim()))
            {
                return Invalid("El código del bien o servicio debe ser del catálogo 54.");
            }

            if (detraction.Percentage is <= 0 or > 100 || decimal.Round(detraction.Percentage, 5) != detraction.Percentage)
            {
                return Invalid("El porcentaje de la detracción debe ser mayor que 0 y no superar 100, con hasta 5 decimales.");
            }

            if (detraction.Amount <= 0 || decimal.Round(detraction.Amount, 2) != detraction.Amount || detraction.Amount > payable
                || Math.Abs(detraction.Amount - (payable * detraction.Percentage / 100m)) >= 1m)
            {
                return Invalid($"El monto de la detracción debe ser positivo, con hasta 2 decimales, y coincidir con el porcentaje del importe total ({payable:0.00}) con un redondeo de hasta un sol.");
            }

            if (detraction.AccountNumber is null || !AccountNumberPattern().IsMatch(detraction.AccountNumber.Trim()))
            {
                return Invalid("Indique el número de cuenta de detracciones en el Banco de la Nación, en la solicitud o en los datos de la empresa (alfanumérico, hasta 100 caracteres).");
            }

            deducted = detraction.Amount;
            return null;
        }

        if (retention!.Percentage is <= 0 or >= 100 || decimal.Round(retention.Percentage, 5) != retention.Percentage || RetainedAmount(payable, retention.Percentage) <= 0)
        {
            return Invalid("El porcentaje de la retención debe ser mayor que 0 y menor que 100, con hasta 5 decimales.");
        }

        deducted = RetainedAmount(payable, retention.Percentage);
        return null;
    }

    /// <summary>The amount withheld: the percentage of the operation amount (the payable amount), in cents (rule 3263).</summary>
    private static decimal RetainedAmount(decimal payable, decimal percentage) =>
        decimal.Round(payable * percentage / 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>Affectation code of the export of goods or services (catalogue 07): tax 9995.</summary>
    private const string ExportAffectation = "40";

    /// <summary>
    /// Operation type (catalogue 51; sheet Factura2_0, rules 2642, 2800): the sale (0101) or the export of goods (0200). An export is an invoice whose lines all have affectation 40;
    /// those lines exist only in an export; its buyer is checked with the export rule.
    /// </summary>
    private static Error? ValidateOperationType(Series series, CreateDocumentRequest request)
    {
        static Error Invalid(string detail) => Error.Validation(ErrorCodes.InvalidDocument, "Tipo de operación inválido", detail);

        var operation = request.OperationTypeCode?.Trim();
        if (operation is not (null or OperationTypes.Sale or OperationTypes.Export) && !OperationTypes.IsDetraction(operation))
        {
            return Invalid($"El tipo de operación '{operation}' no está soportado: 0101 (venta interna), 0200 (exportación de bienes) o 1001 a 1004 (sujetas a detracción).");
        }

        var exportLines = request.Lines.Count(l => l?.Tax?.IgvAffectationCode == ExportAffectation);
        if (OperationTypes.IsDetraction(operation))
        {
            if (request.Detraction is null)
            {
                return Invalid($"El tipo de operación {operation} (sujeta a detracción) requiere los datos de la detracción.");
            }

            var expected = OperationTypes.ForDetraction(request.Detraction.GoodsOrServiceCode);
            if (operation != expected)
            {
                return Invalid($"El código '{request.Detraction.GoodsOrServiceCode?.Trim()}' de la detracción corresponde al tipo de operación {expected}, no a {operation}.");
            }
        }

        if (operation != OperationTypes.Export)
        {
            return exportLines > 0 ? Invalid("Las líneas de exportación (afectación 40) requieren el tipo de operación 0200.") : null;
        }

        if (series.DocumentTypeCode != DocumentTypes.Invoice)
        {
            return Invalid("La exportación se factura con una factura: las boletas de exportación aún no están soportadas.");
        }

        return exportLines == request.Lines.Count ? null : Invalid("Una exportación lleva solo líneas con afectación 40 (regla 2642).");
    }

    /// <summary>
    /// Data of the line that two operation types demand (sheet Factura2_0): every line of a fishing sale (1002) states vessel, species, place and date of unloading and the quantity
    /// (rules 3063, 3130–3135, 3115, 4280, 4281); every line of a cargo transport (1004) states origin, destination, trip detail and the reference values (3116–3126, 4236, 4270). The
    /// other operation types carry neither. The operation type follows the code of the detraction.
    /// </summary>
    private static Error? ValidateLineDetails(CreateDocumentRequest request)
    {
        static Error Invalid(int number, string detail) => Error.Validation(ErrorCodes.InvalidDocument, $"Línea {number} inválida", detail);

        var operation = request.Detraction is { } detraction ? OperationTypes.ForDetraction(detraction.GoodsOrServiceCode) : request.OperationTypeCode?.Trim() ?? OperationTypes.Sale;
        var fishing = operation == OperationTypes.FishingDetraction;
        var cargo = operation == OperationTypes.CargoTransportDetraction;
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            var number = i + 1;
            if (line is null)
            {
                continue;
            }

            if (fishing != (line.Fishing is not null))
            {
                return Invalid(number, fishing
                    ? "Cada línea de una operación de recursos hidrobiológicos (detracción 004, tipo de operación 1002) requiere los datos de la embarcación y de la especie vendida."
                    : "Los datos de recursos hidrobiológicos son solo del tipo de operación 1002 (detracción con el código 004).");
            }

            if (cargo != (line.Transport is not null))
            {
                return Invalid(number, cargo
                    ? "Cada línea de un servicio de transporte de carga (detracción 027, tipo de operación 1004) requiere los datos del transporte."
                    : "Los datos del transporte de carga son solo del tipo de operación 1004 (detracción con el código 027).");
            }

            if (line.Fishing is { } f
                && !(IsText(f.VesselRegistration, 1, 15) && IsText(f.VesselName, 1, 100) && IsText(f.SpeciesType, 1, 150) && IsText(f.UnloadingPlace, 1, 100) && IsAmount(f.SpeciesQuantity)))
            {
                return Invalid(number, "La matrícula (hasta 15 caracteres), el nombre de la embarcación (100), la especie (150) y el lugar de descarga (100) son obligatorios, sin saltos de línea, y la cantidad en toneladas debe ser mayor que cero, con hasta 2 decimales.");
            }

            if (line.Transport is { } t
                && !(IsUbigeo(t.OriginUbigeo) && IsUbigeo(t.DestinationUbigeo) && IsText(t.OriginAddress, 3, 200) && IsText(t.DestinationAddress, 3, 200) && IsText(t.TripDetail, 3, 500)
                    && IsAmount(t.ServiceReferenceValue) && IsAmount(t.EffectiveLoadReferenceValue) && IsAmount(t.NominalLoadReferenceValue)))
            {
                return Invalid(number, "El origen y el destino requieren ubigeo de 6 dígitos y dirección de 3 a 200 caracteres, el detalle del viaje de 3 a 500 y los tres valores referenciales deben ser mayores que cero, con hasta 2 decimales.");
            }
        }

        return null;
    }

    /// <summary>Text of the length the rules ask for, without line breaks, tabs or other control characters.</summary>
    private static bool IsText(string? value, int minLength, int maxLength) =>
        value is not null && value.Trim().Length >= minLength && value.Trim().Length <= maxLength && !value.Any(char.IsControl);

    private static bool IsUbigeo(string? value) => value is { Length: 6 } && value.All(char.IsAsciiDigit);

    /// <summary>Decimal greater than zero of up to 12 integer digits and 2 decimals, n(12,2).</summary>
    private static bool IsAmount(decimal value) => value > 0 && value < 1_000_000_000_000m && decimal.Round(value, 2) == value;

    /// <summary>
    /// Reason 11 (sheet NotaCredito2_0, rules 2642, 3194, 3221, 3107): an adjustment of an export, with export lines only and on an invoice that is one. In any note the lines are all
    /// export or none: a note does not mix the sale of goods in the country with an export.
    /// </summary>
    private static Error? ValidateExportAdjustment(CreateNoteRequest request, Document referenced)
    {
        static Error Invalid(string detail) => Error.Validation(ErrorCodes.InvalidDocument, "Ajuste de exportación inválido", detail);

        var lines = request.Lines ?? [];
        var exportLines = lines.Count(l => l?.Tax?.IgvAffectationCode == ExportAffectation);
        if (request.ReasonCode?.Trim() != "11")
        {
            return exportLines is 0 || exportLines == lines.Count ? null : Invalid("Una nota no mezcla líneas de exportación (afectación 40) con otras.");
        }

        if (exportLines != lines.Count)
        {
            return Invalid("La nota de motivo 11 (ajuste de operaciones de exportación) lleva solo líneas con afectación 40.");
        }

        return referenced.DocumentTypeCode == DocumentTypes.Invoice && JsonSerializer.Deserialize<TaxCalculationResult>(referenced.TotalsJson, Json)!.TotalExport > 0
            ? null
            : Invalid("El motivo 11 solo modifica una factura de exportación.");
    }

    private sealed record StoredLine(TaxableLine? Tax, FishingDetail? Fishing, CargoTransportDetail? Transport);

    private sealed record StoredRequest(
        List<StoredLine?>? Lines, GlobalAdjustments? Adjustments, List<Installment?>? Installments, string? OperationTypeCode, decimal? InitialPayment, Detraction? Detraction, RetentionRequest? Retention);

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
