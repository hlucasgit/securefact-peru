using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SecureFact.Audit.Contracts;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;
using SecureFact.Subscriptions.Contracts;
using SecureFact.Subscriptions.Domain;
using SecureFact.Subscriptions.Infrastructure;
using SecureFact.TaxEngine.Contracts;

namespace SecureFact.Subscriptions.Application;

/// <summary>
/// Issues the invoice (or the receipt) of each charge with the account of the platform that is configured for it, and the credit note when a charge that was invoiced is voided (ADR-065). The
/// document is made by the Billing module as for any other issuer: numbering, tax, signing and sending to SUNAT are its own, so the platform invoices what it charges the way any customer does.
/// </summary>
internal sealed partial class ChargeInvoicing(SubscriptionsDbContext db, IssuerAccess issuer, TimeProvider clock, IAuditTrail audit, ILogger<ChargeInvoicing> logger)
{
    private static readonly string[] MonthNames = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    /// <summary>The settings, when the platform invoices: configured and enabled.</summary>
    public async Task<InvoicingSetting?> ActiveSettingsAsync(CancellationToken cancellationToken)
    {
        var setting = await db.InvoicingSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return setting is { Enabled: true } ? setting : null;
    }

    /// <summary>
    /// Invoices the charges that have none yet and whose tenant gave its billing data, at most <paramref name="max"/> per call. A charge without data waits: the next call, after the tenant
    /// completes them, invoices it. Returns how many it invoiced.
    /// </summary>
    public async Task<int> IssuePendingAsync(DateOnly today, Guid? tenantId, int max, CancellationToken cancellationToken)
    {
        if (await ActiveSettingsAsync(cancellationToken) is not { } settings)
        {
            return 0;
        }

        var pending = await db.Charges.AsNoTracking()
            .Where(c => c.VoidedAt == null && c.TotalAmount > 0m && (tenantId == null || c.TenantId == tenantId)
                && !db.ChargeDocuments.Any(d => d.ChargeId == c.Id && d.Kind == ChargeDocumentKind.Invoice))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Take(max)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        var tenantIds = pending.Select(c => c.TenantId).Distinct().ToList();
        var profiles = (await db.Profiles.AsNoTracking().Where(p => tenantIds.Contains(p.TenantId)).ToListAsync(cancellationToken)).ToDictionary(p => p.TenantId);
        var issued = 0;
        foreach (var charge in pending.Where(c => profiles.ContainsKey(c.TenantId)))
        {
            try
            {
                var result = await IssueInvoiceAsync(charge, profiles[charge.TenantId], settings, today, cancellationToken);
                if (result.IsSuccess)
                {
                    issued++;
                }
                else
                {
                    LogNotInvoiced(logger, charge.Id, result.Error.Code, result.Error.Detail);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One charge that fails must not stop the others; the next pass tries it again. Only its id goes to the log.
                LogInvoiceFailed(logger, exception, charge.Id);
            }
        }

        return issued;
    }

    public async Task<Result<ChargeDocument>> IssueInvoiceAsync(Charge charge, BillingProfile profile, InvoicingSetting settings, DateOnly today, CancellationToken cancellationToken)
    {
        var request = BuildInvoice(charge, profile, settings, today);
        var created = await issuer.RunAsync(settings.IssuerTenantId, services =>
            services.GetRequiredService<IDocumentService>().CreateAsync($"platform-charge-{charge.Id:N}", request, cancellationToken));
        if (!created.IsSuccess)
        {
            return Error.Validation(ErrorCodes.ChargeInvoiceFailed, "No se pudo emitir el comprobante", $"{created.Error.Code}: {created.Error.Detail}");
        }

        var document = created.Value;
        if (document.Totals.PayableAmount != charge.TotalAmount)
        {
            // The charge and the document are computed by different modules; they have to agree to the cent. If they ever do not, the document stands (it is issued) and this says so.
            LogTotalsDiffer(logger, charge.Id, charge.TotalAmount, document.Totals.PayableAmount);
        }

        var saved = await SaveLinkAsync(charge, ChargeDocumentKind.Invoice, settings.IssuerTenantId, document, cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.ChargeInvoiced, "charge", charge.Id.ToString("D"), charge.TenantId,
                NewValues: new Dictionary<string, object?> { ["document"] = $"{document.Series}-{document.Number}", ["type"] = document.DocumentTypeCode, ["total"] = document.Totals.PayableAmount }),
            cancellationToken);
        return saved;
    }

    /// <summary>The credit note that cancels the invoice of a charge that is voided. Idempotent: asking again returns the one that exists.</summary>
    public async Task<Result<ChargeDocument>> IssueCreditNoteAsync(Charge charge, ChargeDocument invoice, InvoicingSetting settings, DateOnly today, CancellationToken cancellationToken)
    {
        if (await db.ChargeDocuments.AsNoTracking().SingleOrDefaultAsync(d => d.ChargeId == charge.Id && d.Kind == ChargeDocumentKind.CreditNote, cancellationToken) is { } existing)
        {
            return existing;
        }

        var receipt = invoice.DocumentTypeCode == "03";
        var request = new CreateNoteRequest(
            receipt ? settings.ReceiptNoteSeriesId : settings.InvoiceNoteSeriesId, invoice.DocumentId, today, "01", "Anulación de la operación: el cargo fue anulado", Lines(charge));
        var created = await issuer.RunAsync(settings.IssuerTenantId, services =>
            services.GetRequiredService<IDocumentService>().CreateNoteAsync($"platform-charge-note-{charge.Id:N}", request, cancellationToken));
        if (!created.IsSuccess)
        {
            return Error.Validation(ErrorCodes.ChargeInvoiceFailed, "No se pudo emitir la nota de crédito", $"{created.Error.Code}: {created.Error.Detail}");
        }

        var saved = await SaveLinkAsync(charge, ChargeDocumentKind.CreditNote, settings.IssuerTenantId, created.Value, cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.ChargeCreditNoted, "charge", charge.Id.ToString("D"), charge.TenantId,
                NewValues: new Dictionary<string, object?> { ["document"] = $"{created.Value.Series}-{created.Value.Number}", ["cancels"] = $"{invoice.Series}-{invoice.Number}" }),
            cancellationToken);
        return saved;
    }

    /// <summary>The state of the electronic document at SUNAT, read in the account of the issuer; null while it is not prepared.</summary>
    public async Task<string?> StateOfAsync(ChargeDocument document, CancellationToken cancellationToken) =>
        await issuer.RunAsync(document.IssuerTenantId, async services =>
        {
            var electronic = await services.GetRequiredService<IElectronicDocumentService>().GetByDocumentAsync(document.DocumentId, cancellationToken);
            return electronic.IsSuccess ? electronic.Value.State.ToString() : null;
        });

    internal static CreateDocumentRequest BuildInvoice(Charge charge, BillingProfile profile, InvoicingSetting settings, DateOnly today)
    {
        var invoice = profile.DocumentTypeCode == "6";
        // A receipt is always paid on the spot; an invoice is sold on credit, with one installment on the due date of the charge, when that date is after the day of issue.
        IReadOnlyList<Installment>? installments = invoice && charge.DueOn > today ? [new Installment(charge.TotalAmount, charge.DueOn)] : null;
        return new CreateDocumentRequest(
            invoice ? settings.InvoiceSeriesId : settings.ReceiptSeriesId,
            today,
            "PEN",
            new BuyerSnapshot(profile.DocumentTypeCode, profile.DocumentNumber, profile.LegalName, profile.Address, profile.Email),
            Lines(charge),
            Installments: installments);
    }

    /// <summary>The fee and, when there is one, the overage, as the amounts the charge computed: the document adds up to the cent to the same total.</summary>
    internal static List<DocumentLineRequest> Lines(Charge charge)
    {
        var month = $"{MonthNames[charge.Period.Month - 1]} de {charge.Period.Year}";
        var lines = new List<DocumentLineRequest>();
        if (charge.MonthlyFee > 0m)
        {
            lines.Add(new DocumentLineRequest($"Servicio de la plataforma SecureFact, plan {charge.PlanName}, {month}", "ZZ", new TaxableLine(1m, charge.MonthlyFee, "10")));
        }

        if (charge.OverageAmount > 0m)
        {
            lines.Add(new DocumentLineRequest($"Comprobantes adicionales de {month}: {charge.OverageDocuments}", "ZZ", new TaxableLine(1m, charge.OverageAmount, "10")));
        }

        return lines;
    }

    private async Task<Result<ChargeDocument>> SaveLinkAsync(Charge charge, ChargeDocumentKind kind, Guid issuerTenantId, DocumentDto document, CancellationToken cancellationToken)
    {
        if (await db.ChargeDocuments.AsNoTracking().SingleOrDefaultAsync(d => d.ChargeId == charge.Id && d.Kind == kind, cancellationToken) is { } existing)
        {
            return existing;
        }

        var link = ChargeDocument.Create(
            charge.TenantId, charge.Id, kind, issuerTenantId, document.Id, document.DocumentTypeCode, document.Series, document.Number, document.IssueDate, document.Totals.PayableAmount, clock.GetUtcNow());
        db.ChargeDocuments.Add(link);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (db.Database.CurrentTransaction is null)
        {
            // Another pass linked the same document first (issuing is idempotent, so it is the same document): its link stands.
            db.Entry(link).State = EntityState.Detached;
            if (await db.ChargeDocuments.AsNoTracking().SingleOrDefaultAsync(d => d.ChargeId == charge.Id && d.Kind == kind, cancellationToken) is { } winner)
            {
                return winner;
            }

            throw;
        }

        return link;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Charge {ChargeId} was not invoiced: {Code} {Detail}")]
    private static partial void LogNotInvoiced(ILogger logger, Guid chargeId, string code, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Invoicing charge {ChargeId} failed unexpectedly.")]
    private static partial void LogInvoiceFailed(ILogger logger, Exception exception, Guid chargeId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The document of charge {ChargeId} has a total of {DocumentTotal} and the charge {ChargeTotal}.")]
    private static partial void LogTotalsDiffer(ILogger logger, Guid chargeId, decimal chargeTotal, decimal documentTotal);
}
