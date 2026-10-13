using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SecureFact.CpeEngine.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.SharedKernel.Export;

namespace SecureFact.CpeEngine.Application;

/// <summary>
/// The answer of SUNAT to each document, and the files that matter in the end: the signed XML as it was kept and sent, and the CDR as SUNAT returned it (ADR-074). Nothing is generated again:
/// these are the immutable bytes of ADR-036 (phase 1: the database still holds them).
/// </summary>
internal sealed class CpeAccountData(CpeDbContext db) : IAccountDataContributor
{
    /// <summary>A small batch, because every row carries its XML and its CDR.</summary>
    private const int FileBatch = 20;

    public async Task WriteAsync(IAccountDataWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        await writer.WriteTableAsync(
            "envios_a_sunat",
            ["comprobante_id", "empresa_id", "archivo", "tipo", "serie", "numero", "fecha_emision", "estado", "intentos", "ticket", "codigo_respuesta", "descripcion_respuesta", "huella", "tiene_cdr", "error_codigo", "error_mensaje", "creado", "actualizado"],
            db.ElectronicDocuments.AsNoTracking().OrderBy(e => e.Id)
                .Select(e => new { e.DocumentId, e.CompanyId, e.FileBaseName, e.DocumentTypeCode, e.Series, e.Number, e.IssueDate, e.State, e.Attempts, e.Ticket, e.CdrResponseCode, e.CdrDescription, e.DigestValue, HasCdr = e.CdrZip != null, e.LastErrorCode, e.LastErrorMessage, e.CreatedAt, e.UpdatedAt })
                .InBatchesAsync(500, cancellationToken)
                .Select(e => (IReadOnlyList<string?>)[
                    e.DocumentId.ToString("D"), e.CompanyId.ToString("D"), e.FileBaseName, e.DocumentTypeCode, e.Series, AccountCsv.Number(e.Number), AccountCsv.Day(e.IssueDate), e.State.ToString(), AccountCsv.Number(e.Attempts), e.Ticket,
                    e.CdrResponseCode?.ToString(CultureInfo.InvariantCulture), e.CdrDescription, e.DigestValue, AccountCsv.YesNo(e.HasCdr), e.LastErrorCode, e.LastErrorMessage, AccountCsv.Stamp(e.CreatedAt), AccountCsv.Stamp(e.UpdatedAt)]),
            cancellationToken);

        await foreach (var file in db.ElectronicDocuments.AsNoTracking().OrderBy(e => e.Id).Select(e => new { e.FileBaseName, e.SignedXml, e.CdrZip }).InBatchesAsync(FileBatch, cancellationToken))
        {
            await writer.WriteFileAsync($"xml/{file.FileBaseName}.xml", AccountCsv.Bytes(file.SignedXml), cancellationToken);
            if (file.CdrZip is { Length: > 0 } cdr)
            {
                await writer.WriteFileAsync($"cdr/R-{file.FileBaseName}.zip", cdr, cancellationToken);
            }
        }
    }
}
