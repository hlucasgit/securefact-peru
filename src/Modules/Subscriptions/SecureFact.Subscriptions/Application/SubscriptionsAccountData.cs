using Microsoft.EntityFrameworkCore;
using SecureFact.Platform.Persistence;
using SecureFact.SharedKernel.Export;
using SecureFact.Subscriptions.Infrastructure;

namespace SecureFact.Subscriptions.Application;

/// <summary>
/// What the account was charged and what it paid, and its billing data (ADR-074). Not the commission of its reseller or the terms between them, which are the reseller's, and never the keys of the
/// gateway.
/// </summary>
internal sealed class SubscriptionsAccountData(SubscriptionsDbContext db) : IAccountDataContributor
{
    private const int Batch = 500;

    public async Task WriteAsync(IAccountDataWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        await writer.WriteTableAsync(
            "cargos",
            [
                "id", "mes", "plan_codigo", "plan_nombre", "cuota_mensual", "comprobantes_incluidos", "comprobantes_emitidos", "comprobantes_excedentes", "precio_por_excedente", "monto_excedente", "monto_neto", "tasa_igv", "igv", "total", "moneda",
                "emitido", "vence", "suspension", "anulado", "motivo_de_anulacion", "creado",
            ],
            db.Charges.AsNoTracking().OrderBy(c => c.Id).InBatchesAsync(Batch, cancellationToken)
                .Select(c => (IReadOnlyList<string?>)[
                    c.Id.ToString("D"), AccountCsv.Day(c.Period), c.PlanCode, c.PlanName, AccountCsv.Number(c.MonthlyFee), c.IncludedDocuments?.ToString(System.Globalization.CultureInfo.InvariantCulture), AccountCsv.Number(c.DocumentsIssued),
                    AccountCsv.Number(c.OverageDocuments), AccountCsv.Number(c.OverageUnitPrice), AccountCsv.Number(c.OverageAmount), AccountCsv.Number(c.NetAmount), AccountCsv.Number(c.TaxRate), AccountCsv.Number(c.TaxAmount), AccountCsv.Number(c.TotalAmount),
                    c.Currency, AccountCsv.Day(c.IssuedOn), AccountCsv.Day(c.DueOn), AccountCsv.Day(c.SuspendOn), AccountCsv.Stamp(c.VoidedAt), c.VoidReason, AccountCsv.Stamp(c.CreatedAt)]),
            cancellationToken);
        await writer.WriteTableAsync(
            "pagos",
            ["id", "cargo_id", "monto", "medio", "referencia", "pagado", "nota", "revierte_el_pago", "registrado"],
            db.Payments.AsNoTracking().OrderBy(p => p.Id).InBatchesAsync(Batch, cancellationToken)
                .Select(p => (IReadOnlyList<string?>)[p.Id.ToString("D"), p.ChargeId.ToString("D"), AccountCsv.Number(p.Amount), p.Method.ToString(), p.Reference, AccountCsv.Day(p.PaidOn), p.Note, p.ReversesPaymentId?.ToString("D"), AccountCsv.Stamp(p.RecordedAt)]),
            cancellationToken);
        await writer.WriteTableAsync(
            "intentos_de_pago_con_tarjeta",
            ["id", "cargo_id", "monto", "usuario_id", "estado", "referencia_de_la_pasarela", "codigo", "mensaje", "pago_id", "iniciado", "terminado", "resolucion"],
            db.PaymentAttempts.AsNoTracking().OrderBy(a => a.Id).InBatchesAsync(Batch, cancellationToken)
                .Select(a => (IReadOnlyList<string?>)[a.Id.ToString("D"), a.ChargeId.ToString("D"), AccountCsv.Number(a.Amount), a.UserId?.ToString("D"), a.State.ToString(), a.GatewayChargeId, a.Code, a.Message, a.PaymentId?.ToString("D"), AccountCsv.Stamp(a.CreatedAt), AccountCsv.Stamp(a.CompletedAt), a.Resolution]),
            cancellationToken);
        await writer.WriteTableAsync(
            "datos_de_facturacion",
            ["tipo_documento", "documento", "razon_social", "direccion", "correo", "actualizado"],
            db.Profiles.AsNoTracking().OrderBy(p => p.TenantId).InBatchesAsync(Batch, cancellationToken)
                .Select(p => (IReadOnlyList<string?>)[p.DocumentTypeCode, p.DocumentNumber, p.LegalName, p.Address, p.Email, AccountCsv.Stamp(p.UpdatedAt)]),
            cancellationToken);
    }
}
