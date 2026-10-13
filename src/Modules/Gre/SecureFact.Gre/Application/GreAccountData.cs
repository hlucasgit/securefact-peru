using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SecureFact.Gre.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.SharedKernel.Export;

namespace SecureFact.Gre.Application;

/// <summary>The despatch advices of the account: the series, each guide with the answer of SUNAT, and its signed XML and CDR (ADR-074).</summary>
internal sealed class GreAccountData(GreDbContext db) : IAccountDataContributor
{
    private const int FileBatch = 20;

    public async Task WriteAsync(IAccountDataWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        await writer.WriteTableAsync(
            "series_de_guias",
            ["id", "empresa_id", "tipo", "serie", "ultimo_numero", "activa", "creada"],
            db.Series.AsNoTracking().OrderBy(s => s.Id).InBatchesAsync(500, cancellationToken)
                .Select(s => (IReadOnlyList<string?>)[s.Id.ToString("D"), s.CompanyId.ToString("D"), s.DocumentTypeCode, s.Code, AccountCsv.Number(s.LastNumber), AccountCsv.YesNo(s.IsActive), AccountCsv.Stamp(s.CreatedAt)]),
            cancellationToken);
        await writer.WriteTableAsync(
            "guias",
            ["id", "empresa_id", "archivo", "tipo", "serie", "numero", "fecha_emision", "motivo", "modalidad", "destinatario_documento", "destinatario_nombre", "estado", "intentos", "ticket", "codigo_respuesta", "descripcion_respuesta", "huella", "tiene_cdr", "error_codigo", "error_mensaje", "creada", "enviada", "procesada"],
            db.Guides.AsNoTracking().OrderBy(g => g.Id)
                .Select(g => new { g.Id, g.CompanyId, g.FileBaseName, g.DocumentTypeCode, g.Series, g.Number, g.IssueDate, g.MotiveCode, g.ModalityCode, g.RecipientDocument, g.RecipientName, g.State, g.Attempts, g.Ticket, g.CdrResponseCode, g.CdrDescription, g.DigestValue, HasCdr = g.CdrZip != null, g.ErrorCode, g.ErrorMessage, g.CreatedAt, g.SentAt, g.ProcessedAt })
                .InBatchesAsync(500, cancellationToken)
                .Select(g => (IReadOnlyList<string?>)[
                    g.Id.ToString("D"), g.CompanyId.ToString("D"), g.FileBaseName, g.DocumentTypeCode, g.Series, AccountCsv.Number(g.Number), AccountCsv.Day(g.IssueDate), g.MotiveCode, g.ModalityCode, g.RecipientDocument, g.RecipientName,
                    g.State.ToString(), AccountCsv.Number(g.Attempts), g.Ticket, g.CdrResponseCode?.ToString(CultureInfo.InvariantCulture), g.CdrDescription, g.DigestValue, AccountCsv.YesNo(g.HasCdr), g.ErrorCode, g.ErrorMessage,
                    AccountCsv.Stamp(g.CreatedAt), AccountCsv.Stamp(g.SentAt), AccountCsv.Stamp(g.ProcessedAt)]),
            cancellationToken);

        await foreach (var file in db.Guides.AsNoTracking().OrderBy(g => g.Id).Select(g => new { g.FileBaseName, g.SignedXml, g.CdrZip }).InBatchesAsync(FileBatch, cancellationToken))
        {
            await writer.WriteFileAsync($"guias/xml/{file.FileBaseName}.xml", AccountCsv.Bytes(file.SignedXml), cancellationToken);
            if (file.CdrZip is { Length: > 0 } cdr)
            {
                await writer.WriteFileAsync($"guias/cdr/R-{file.FileBaseName}.zip", cdr, cancellationToken);
            }
        }
    }
}
