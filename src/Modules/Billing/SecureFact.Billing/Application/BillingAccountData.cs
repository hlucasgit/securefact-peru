using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SecureFact.Billing.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.SharedKernel.Export;

namespace SecureFact.Billing.Application;

/// <summary>The series, the documents and their lines (ADR-074). The signed XML and the answer of SUNAT are the ones of the CpeEngine module.</summary>
internal sealed class BillingAccountData(BillingDbContext db) : IAccountDataContributor
{
    private const int Batch = 500;

    public async Task WriteAsync(IAccountDataWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        await writer.WriteTableAsync(
            "series",
            ["id", "empresa_id", "establecimiento_id", "tipo", "serie", "ultimo_numero", "activa", "creada"],
            db.Series.AsNoTracking().OrderBy(s => s.Id).InBatchesAsync(Batch, cancellationToken)
                .Select(s => (IReadOnlyList<string?>)[s.Id.ToString("D"), s.CompanyId.ToString("D"), s.EstablishmentId?.ToString("D"), s.DocumentTypeCode, s.Code, AccountCsv.Number(s.LastNumber), AccountCsv.YesNo(s.IsActive), AccountCsv.Stamp(s.CreatedAt)]),
            cancellationToken);
        await writer.WriteTableAsync(
            "comprobantes",
            [
                "id", "empresa_id", "tipo", "serie", "numero", "fecha_emision", "moneda", "comprador_tipo_documento", "comprador_documento", "comprador_nombre", "comprador_direccion", "comprador_correo", "estado", "total",
                "referencia_tipo", "referencia_serie", "referencia_numero", "motivo_codigo", "motivo", "creado",
            ],
            db.Documents.AsNoTracking().OrderBy(d => d.Id).InBatchesAsync(Batch, cancellationToken)
                .Select(d => (IReadOnlyList<string?>)[
                    d.Id.ToString("D"), d.CompanyId.ToString("D"), d.DocumentTypeCode, d.SeriesCode, AccountCsv.Number(d.Number), AccountCsv.Day(d.IssueDate), d.Currency, d.BuyerDocumentTypeCode, d.BuyerDocumentNumber, d.BuyerName,
                    d.BuyerAddress, d.BuyerEmail, d.Status.ToString(), AccountCsv.Number(d.PayableAmount), d.ReferencedDocumentTypeCode, d.ReferencedSeries, d.ReferencedNumber?.ToString(CultureInfo.InvariantCulture), d.ReasonCode, d.Reason,
                    AccountCsv.Stamp(d.CreatedAt)]),
            cancellationToken);
        await writer.WriteTableAsync(
            "lineas_de_comprobantes",
            ["comprobante_id", "linea", "descripcion", "unidad", "codigo_producto", "cantidad", "valor_unitario", "afectacion_igv", "valor_de_venta", "tributo", "monto_del_tributo", "precio_unitario_con_impuestos"],
            db.DocumentLines.AsNoTracking().OrderBy(l => l.DocumentId).ThenBy(l => l.LineNumber).InBatchesAsync(Batch, cancellationToken)
                .Select(l => (IReadOnlyList<string?>)[
                    l.DocumentId.ToString("D"), AccountCsv.Number(l.LineNumber), l.Description, l.UnitCode, l.ProductCode, AccountCsv.Number(l.Quantity), AccountCsv.Number(l.UnitValue), l.AffectationCode,
                    AccountCsv.Number(l.LineExtensionAmount), l.TaxCode, AccountCsv.Number(l.TotalTaxAmount), AccountCsv.Number(l.UnitPriceIncludingTaxes)]),
            cancellationToken);
    }
}
