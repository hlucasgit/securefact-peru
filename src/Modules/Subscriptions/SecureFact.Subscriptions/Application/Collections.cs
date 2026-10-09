using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Notifications.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;
using SecureFact.Subscriptions.Contracts;
using SecureFact.Subscriptions.Domain;
using SecureFact.Subscriptions.Infrastructure;

namespace SecureFact.Subscriptions.Application;

internal sealed class Collections(SubscriptionsDbContext db, IDataScope scope, TimeProvider clock, IAuditTrail audit, Enforcement enforcement, Commissions commissions, ChargeInvoicing invoicing, IBillingNotices notices) : ICollections
{
    private const int MaxPageSize = 100;
    private const int MinReasonLength = 3;
    private const int MaxReasonLength = 300;
    private const int MaxReferenceLength = 100;

    private static readonly Error ChargeMissing = Error.NotFound(ErrorCodes.ChargeNotFound, "Cargo no encontrado", "El cargo no existe o no es visible para este contexto.");

    private sealed class Row
    {
        public required Charge Charge { get; init; }

        public decimal Paid { get; init; }
    }

    public async Task<Result<IReadOnlyList<ChargeDto>>> ListChargesAsync(ChargeFilter filter, CancellationToken cancellationToken)
    {
        if (scope.Kind is not (DataScopeKind.Platform or DataScopeKind.Tenant))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Se requiere una cuenta o la plataforma.");
        }

        var today = LimaCalendar.Today(clock.GetUtcNow());
        var query = Rows();
        if (scope.Kind == DataScopeKind.Platform && filter.TenantId is { } tenantId)
        {
            query = query.Where(r => r.Charge.TenantId == tenantId);
        }

        if (filter.Period is { } period)
        {
            var month = LimaCalendar.MonthStart(period);
            query = query.Where(r => r.Charge.Period == month);
        }

        if (filter.Status is { } status)
        {
            query = ByStatus(query, status, today);
        }

        var rows = await query.OrderByDescending(r => r.Charge.Period).ThenBy(r => r.Charge.TenantName).ThenBy(r => r.Charge.Id)
            .Skip(Math.Max(filter.Skip, 0)).Take(Math.Clamp(filter.Take, 1, MaxPageSize)).ToListAsync(cancellationToken);
        var ids = rows.Select(r => r.Charge.Id).ToList();
        var invoices = (await db.ChargeDocuments.AsNoTracking().Where(d => ids.Contains(d.ChargeId) && d.Kind == ChargeDocumentKind.Invoice).Select(d => new { d.ChargeId, d.Series, d.Number }).ToListAsync(cancellationToken))
            .ToDictionary(d => d.ChargeId, d => $"{d.Series}-{d.Number}");
        return rows.Select(r => ToDto(r.Charge, r.Paid, today) with { Invoice = invoices.GetValueOrDefault(r.Charge.Id) }).ToList();
    }

    public async Task<Result<ChargeDetailDto>> GetChargeAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await Rows().SingleOrDefaultAsync(r => r.Charge.Id == id, cancellationToken);
        if (row is null)
        {
            return ChargeMissing;
        }

        var payments = await db.Payments.AsNoTracking().Where(p => p.ChargeId == id).OrderBy(p => p.RecordedAt).ThenBy(p => p.Id).ToListAsync(cancellationToken);
        var documents = new List<ChargeDocumentDto>();
        foreach (var document in await db.ChargeDocuments.AsNoTracking().Where(d => d.ChargeId == id).OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).ToListAsync(cancellationToken))
        {
            documents.Add(new ChargeDocumentDto(document.Id, document.ChargeId, document.Kind, document.DocumentTypeCode, document.Series, document.Number, document.IssueDate, document.Total, await invoicing.StateOfAsync(document, cancellationToken)));
        }

        var invoice = documents.FirstOrDefault(d => d.Kind == ChargeDocumentKind.Invoice)?.Name;
        return new ChargeDetailDto(ToDto(row.Charge, row.Paid, LimaCalendar.Today(clock.GetUtcNow())) with { Invoice = invoice }, payments.Select(ToDto).ToList(), documents);
    }

    public async Task<Result<PaymentDto>> RecordPaymentAsync(Guid chargeId, RecordPaymentRequest request, CancellationToken cancellationToken)
    {
        if (RequirePlatform() is { } denied)
        {
            return denied;
        }

        var now = clock.GetUtcNow();
        var today = LimaCalendar.Today(now);
        if (ValidatePayment(request, today) is { } invalid)
        {
            return invalid;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(chargeId, cancellationToken);
        var charge = await db.Charges.AsNoTracking().SingleOrDefaultAsync(c => c.Id == chargeId, cancellationToken);
        if (charge is null)
        {
            return ChargeMissing;
        }

        if (charge.IsVoid)
        {
            return Error.Validation(ErrorCodes.InvalidPayment, "Cargo anulado", "Un cargo anulado no recibe pagos.");
        }

        if (request.PaidOn < charge.IssuedOn)
        {
            return Error.Validation(ErrorCodes.InvalidPayment, "Fecha de pago inválida", "El pago no puede ser anterior a la emisión del cargo.");
        }

        var paid = await db.Payments.Where(p => p.ChargeId == chargeId).SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;
        var balance = charge.TotalAmount - paid;
        if (request.Amount > balance)
        {
            return Error.Validation(ErrorCodes.InvalidPayment, "Monto superior al saldo", $"El saldo del cargo es S/ {balance:0.00}: un pago no lo supera.");
        }

        var payment = Payment.Create(charge.TenantId, charge.Id, request.Amount, request.Method, Clean(request.Reference), request.PaidOn, Clean(request.Note), null, now);
        db.Payments.Add(payment);
        await db.SaveChangesAsync(cancellationToken);
        await commissions.AccrueAsync(charge, payment, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.PaymentRecorded, "payment", payment.Id.ToString("D"), charge.TenantId,
                NewValues: new Dictionary<string, object?> { ["charge"] = charge.Id, ["period"] = LimaCalendar.PeriodName(charge.Period), ["amount"] = payment.Amount, ["method"] = payment.Method, ["reference"] = payment.Reference }),
            cancellationToken);
        await enforcement.ReactivateIfClearedAsync(new TenantId(charge.TenantId), today, cancellationToken);
        await notices.SendAsync(
            new BillingNotice(
                BillingNoticeKind.PaymentReceived, charge.TenantId, charge.Id, LimaCalendar.PeriodName(charge.Period), charge.TotalAmount, balance - payment.Amount, charge.Currency, charge.DueOn, charge.SuspendOn,
                payment.Id, payment.Amount),
            cancellationToken);
        return ToDto(payment);
    }

    public async Task<Result<PaymentDto>> ReversePaymentAsync(Guid paymentId, string reason, CancellationToken cancellationToken)
    {
        if (RequirePlatform() is { } denied)
        {
            return denied;
        }

        var why = reason?.Trim() ?? string.Empty;
        if (why.Length is < MinReasonLength or > MaxReasonLength)
        {
            return Error.Validation(ErrorCodes.InvalidPayment, "Motivo inválido", $"El motivo debe tener entre {MinReasonLength} y {MaxReasonLength} caracteres.");
        }

        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var original = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
        if (original is null)
        {
            return Error.NotFound(ErrorCodes.PaymentNotFound, "Pago no encontrado", "El pago no existe.");
        }

        await LockAsync(original.ChargeId, cancellationToken);
        if (original.Amount <= 0 || original.ReversesPaymentId is not null)
        {
            return Error.Validation(ErrorCodes.InvalidPayment, "Pago no reversible", "Solo se revierte un pago recibido, no una reversa.");
        }

        if (await db.Payments.AnyAsync(p => p.ReversesPaymentId == paymentId, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.InvalidPayment, "Pago ya revertido", "Este pago ya fue revertido.");
        }

        var charge = await db.Charges.AsNoTracking().SingleAsync(c => c.Id == original.ChargeId, cancellationToken);
        var reversal = Payment.Create(original.TenantId, original.ChargeId, -original.Amount, original.Method, original.Reference, LimaCalendar.Today(now), why, original.Id, now);
        db.Payments.Add(reversal);
        await db.SaveChangesAsync(cancellationToken);
        await commissions.AccrueAsync(charge, reversal, original.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.PaymentReversed, "payment", reversal.Id.ToString("D"), original.TenantId,
                NewValues: new Dictionary<string, object?> { ["reverses"] = original.Id, ["charge"] = original.ChargeId, ["amount"] = reversal.Amount, ["reason"] = why }),
            cancellationToken);
        return ToDto(reversal);
    }

    public async Task<Result<ChargeDto>> VoidChargeAsync(Guid id, string reason, CancellationToken cancellationToken)
    {
        if (RequirePlatform() is { } denied)
        {
            return denied;
        }

        var why = reason?.Trim() ?? string.Empty;
        if (why.Length is < MinReasonLength or > MaxReasonLength)
        {
            return Error.Validation(ErrorCodes.InvalidPayment, "Motivo inválido", $"El motivo debe tener entre {MinReasonLength} y {MaxReasonLength} caracteres.");
        }

        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(id, cancellationToken);
        var charge = await db.Charges.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (charge is null)
        {
            return ChargeMissing;
        }

        if (charge.IsVoid)
        {
            return Error.Conflict(ErrorCodes.InvalidPayment, "Cargo ya anulado", "El cargo ya estaba anulado.");
        }

        var paid = await db.Payments.Where(p => p.ChargeId == id).SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;
        if (paid != 0m)
        {
            return Error.Validation(ErrorCodes.InvalidPayment, "Cargo con pagos", "El cargo tiene pagos: revierta primero los pagos para anularlo.");
        }

        // A charge that was invoiced is cancelled with a credit note first: voiding it without one would leave an invoice for something that is no longer owed.
        var today = LimaCalendar.Today(now);
        if (await db.ChargeDocuments.AsNoTracking().SingleOrDefaultAsync(d => d.ChargeId == id && d.Kind == ChargeDocumentKind.Invoice, cancellationToken) is { } invoiced)
        {
            if (await invoicing.ActiveSettingsAsync(cancellationToken) is not { } settings)
            {
                return Error.Validation(ErrorCodes.ChargeInvoiceFailed, "Cargo facturado", "El cargo tiene factura o boleta emitida: para anularlo hace falta la configuración de facturación de la plataforma, que emite la nota de crédito.");
            }

            var note = await invoicing.IssueCreditNoteAsync(charge, invoiced, settings, today, cancellationToken);
            if (!note.IsSuccess)
            {
                return note.Error;
            }
        }

        charge.MarkVoid(why, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.ChargeVoided, "charge", charge.Id.ToString("D"), charge.TenantId,
                NewValues: new Dictionary<string, object?> { ["period"] = LimaCalendar.PeriodName(charge.Period), ["total"] = charge.TotalAmount, ["reason"] = why }),
            cancellationToken);
        await enforcement.ReactivateIfClearedAsync(new TenantId(charge.TenantId), today, cancellationToken);
        return ToDto(charge, 0m, today);
    }

    private IQueryable<Row> Rows() =>
        db.Charges.AsNoTracking().Select(c => new Row { Charge = c, Paid = db.Payments.Where(p => p.ChargeId == c.Id).Sum(p => (decimal?)p.Amount) ?? 0m });

    private static IQueryable<Row> ByStatus(IQueryable<Row> query, ChargeStatus status, DateOnly today) => status switch
    {
        ChargeStatus.Void => query.Where(r => r.Charge.VoidedAt != null),
        ChargeStatus.Paid => query.Where(r => r.Charge.VoidedAt == null && r.Paid >= r.Charge.TotalAmount),
        ChargeStatus.Overdue => query.Where(r => r.Charge.VoidedAt == null && r.Paid < r.Charge.TotalAmount && r.Charge.DueOn < today),
        ChargeStatus.Partial => query.Where(r => r.Charge.VoidedAt == null && r.Paid > 0m && r.Paid < r.Charge.TotalAmount && r.Charge.DueOn >= today),
        _ => query.Where(r => r.Charge.VoidedAt == null && r.Paid <= 0m && r.Paid < r.Charge.TotalAmount && r.Charge.DueOn >= today),
    };

    internal static ChargeStatus StatusOf(Charge charge, decimal paid, DateOnly today) =>
        charge.IsVoid ? ChargeStatus.Void
        : paid >= charge.TotalAmount ? ChargeStatus.Paid
        : charge.DueOn < today ? ChargeStatus.Overdue
        : paid > 0m ? ChargeStatus.Partial
        : ChargeStatus.Pending;

    internal static ChargeDto ToDto(Charge c, decimal paid, DateOnly today) => new(
        c.Id, c.TenantId, c.TenantName, c.Period, c.PlanCode, c.PlanName, c.MonthlyFee, c.IncludedDocuments, c.DocumentsIssued, c.OverageDocuments, c.OverageUnitPrice, c.OverageAmount, c.NetAmount,
        c.TaxRate, c.TaxAmount, c.TotalAmount, c.Currency, c.IssuedOn, c.DueOn, c.SuspendOn, paid, c.IsVoid ? 0m : c.TotalAmount - paid, StatusOf(c, paid, today), c.VoidReason, c.CreatedAt);

    private static PaymentDto ToDto(Payment p) => new(p.Id, p.ChargeId, p.Amount, p.Method, p.Reference, p.PaidOn, p.Note, p.ReversesPaymentId, p.RecordedAt);

    private static Error? ValidatePayment(RecordPaymentRequest request, DateOnly today)
    {
        if (request.Amount <= 0m || decimal.Round(request.Amount, 2) != request.Amount)
        {
            return Error.Validation(ErrorCodes.InvalidPayment, "Monto inválido", "El monto es mayor que cero y tiene hasta dos decimales.");
        }

        if (!Enum.IsDefined(request.Method))
        {
            return Error.Validation(ErrorCodes.InvalidPayment, "Medio de pago inválido", "El medio de pago no existe.");
        }

        if (request.PaidOn > today)
        {
            return Error.Validation(ErrorCodes.InvalidPayment, "Fecha de pago inválida", "El pago no puede tener fecha futura.");
        }

        return request.Reference?.Trim().Length > MaxReferenceLength || request.Note?.Trim().Length > MaxReasonLength
            ? Error.Validation(ErrorCodes.InvalidPayment, "Texto demasiado largo", $"La referencia tiene hasta {MaxReferenceLength} caracteres y la nota hasta {MaxReasonLength}.")
            : null;
    }

    /// <summary>Payments, reversals and the void mark of a charge take turns: two recorded together cannot both fit in the same balance.</summary>
    private Task<int> LockAsync(Guid chargeId, CancellationToken cancellationToken)
    {
        var key = $"charge:{chargeId:N}";
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private Error? RequirePlatform() =>
        scope.Kind == DataScopeKind.Platform ? null : Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Solo la plataforma registra pagos y anula cargos.");
}
