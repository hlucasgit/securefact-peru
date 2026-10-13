using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine.Contracts;
using SecureFact.CpeEngine.Infrastructure;
using SecureFact.Organizations.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Export;
using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine.Application;

internal sealed class DocumentExport(CpeDbContext db, IDataScope scope, IDocumentService billing, ICompanyAdministration companies, IAuditTrail audit) : IDocumentExport
{
    private static readonly string[] Header =
    [
        "archivo", "tipo", "serie", "numero", "fecha_emision", "estado", "codigo_respuesta_sunat", "descripcion_sunat", "comprador_tipo_documento", "comprador_documento", "comprador_nombre", "moneda", "total", "huella", "tiene_cdr",
    ];

    public async Task<Result<DocumentExportResult>> ExportAsync(DocumentExportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (scope.Kind != DataScopeKind.Tenant)
        {
            return Error.Forbidden(ErrorCodes.TenantNotResolved, "Tenant requerido", "La exportación es de una cuenta: requiere un contexto de tenant.");
        }

        if (request.To < request.From || request.To.DayNumber - request.From.DayNumber >= IDocumentExport.MaxDays)
        {
            return Error.Validation(ErrorCodes.InvalidExport, "Período inválido", $"El período va de la fecha inicial a la final, de {IDocumentExport.MaxDays} días como máximo.");
        }

        var type = string.IsNullOrWhiteSpace(request.DocumentTypeCode) ? null : request.DocumentTypeCode.Trim();
        if (type is not null && type.Length != 2)
        {
            return Error.Validation(ErrorCodes.InvalidExport, "Tipo de comprobante inválido", "El tipo es el código del catálogo 01 (por ejemplo 01, 03, 07 u 08).");
        }

        var company = await companies.GetAsync(request.CompanyId, cancellationToken);
        if (!company.IsSuccess)
        {
            return company.Error;
        }

        var query = db.ElectronicDocuments.AsNoTracking().Where(e => e.CompanyId == request.CompanyId && e.IssueDate >= request.From && e.IssueDate <= request.To);
        if (type is not null)
        {
            query = query.Where(e => e.DocumentTypeCode == type);
        }

        if (await query.CountAsync(cancellationToken) > IDocumentExport.MaxDocuments)
        {
            return Error.Validation(ErrorCodes.ExportTooLarge, "Demasiados comprobantes", $"El período tiene más de {IDocumentExport.MaxDocuments} comprobantes: pida un período más corto o un solo tipo.");
        }

        var rows = await query.OrderBy(e => e.IssueDate).ThenBy(e => e.DocumentTypeCode).ThenBy(e => e.Series).ThenBy(e => e.Number)
            .Select(e => new { e.DocumentId, e.FileBaseName, e.DocumentTypeCode, e.Series, e.Number, e.IssueDate, e.State, e.CdrResponseCode, e.CdrDescription, e.DigestValue, e.SignedXml, e.CdrZip })
            .ToListAsync(cancellationToken);

        await using var output = new MemoryStream();
        var withCdr = 0;
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var index = new StringBuilder("﻿").Append(string.Join(',', Header)).Append("\r\n");
            foreach (var row in rows)
            {
                await AddAsync(zip, $"xml/{row.FileBaseName}.xml", Encoding.UTF8.GetBytes(row.SignedXml), cancellationToken);
                if (row.CdrZip is { Length: > 0 } cdr)
                {
                    withCdr++;
                    await AddAsync(zip, $"cdr/R-{row.FileBaseName}.zip", cdr, cancellationToken);
                }

                var document = await billing.GetAsync(row.DocumentId, cancellationToken);
                index.Append(string.Join(',', Cells(row.FileBaseName, row.DocumentTypeCode, row.Series, row.Number.ToString(CultureInfo.InvariantCulture), row.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    row.State.ToString(), row.CdrResponseCode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, row.CdrDescription ?? string.Empty,
                    document.IsSuccess ? document.Value.Buyer.DocumentTypeCode : string.Empty, document.IsSuccess ? document.Value.Buyer.DocumentNumber : string.Empty, document.IsSuccess ? document.Value.Buyer.Name : string.Empty,
                    document.IsSuccess ? document.Value.Currency : string.Empty, document.IsSuccess ? document.Value.Totals.PayableAmount.ToString("0.00", CultureInfo.InvariantCulture) : string.Empty,
                    row.DigestValue, row.CdrZip is { Length: > 0 } ? "si" : "no"))).Append("\r\n");
            }

            await AddAsync(zip, "indice.csv", Encoding.UTF8.GetBytes(index.ToString()), cancellationToken);
        }

        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.DocumentsExported, "company", request.CompanyId.ToString("D"), company.Value.TenantId,
                NewValues: new Dictionary<string, object?>
                {
                    ["from"] = request.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["to"] = request.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["type"] = type,
                    ["documents"] = rows.Count,
                    ["withCdr"] = withCdr,
                }),
            cancellationToken);
        var name = $"comprobantes-{company.Value.Ruc}-{request.From:yyyyMMdd}-{request.To:yyyyMMdd}{(type is null ? string.Empty : $"-{type}")}.zip";
        return new DocumentExportResult(output.ToArray(), name, rows.Count, withCdr);
    }

    private static async Task AddAsync(ZipArchive zip, string name, byte[] content, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(content, cancellationToken);
    }

    private static IEnumerable<string> Cells(params string[] values) => values.Select(AccountCsv.Cell);
}
