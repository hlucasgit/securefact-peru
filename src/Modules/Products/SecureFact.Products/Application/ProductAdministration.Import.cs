using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.Products.Contracts;
using SecureFact.Products.Domain;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Import;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Products.Application;

internal sealed partial class ProductAdministration
{
    /// <summary>What the same column is called in a spreadsheet: «bien», «servicio» and the names of the enumeration.</summary>
    private static readonly Dictionary<string, ProductKind> KindNames = new()
    {
        ["bien"] = ProductKind.Goods,
        ["bienes"] = ProductKind.Goods,
        ["goods"] = ProductKind.Goods,
        ["producto"] = ProductKind.Goods,
        ["servicio"] = ProductKind.Service,
        ["servicios"] = ProductKind.Service,
        ["service"] = ProductKind.Service,
    };

    public async Task<Result<ImportResult>> ImportAsync(ImportRequest request, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Tenant || scope.Current is not { } tenant)
        {
            return Error.Forbidden(ErrorCodes.TenantNotResolved, "Tenant requerido", "Esta operación requiere un contexto de tenant.");
        }

        ArgumentNullException.ThrowIfNull(request);
        var parsed = CsvParser.Parse(request.Csv);
        if (!parsed.IsSuccess)
        {
            return parsed.Error;
        }

        var table = parsed.Value;
        var code = table.IndexOf("codigo", "codigo_interno", "internal_code", "cod");
        var description = table.IndexOf("descripcion", "description", "producto", "nombre");
        var value = table.IndexOf("valor_unitario", "unit_value", "valor");
        var affectation = table.IndexOf("afectacion_igv", "afectacion", "igv_affectation", "tipo_afectacion");
        if (code < 0 || description < 0 || value < 0 || affectation < 0)
        {
            return Error.Validation(ErrorCodes.ImportFileInvalid, "Archivo inválido", "Faltan columnas obligatorias: «codigo», «descripcion», «valor_unitario» (sin IGV) y «afectacion_igv» (código del catálogo 07). El encabezado debe estar en la primera línea.");
        }

        var kind = table.IndexOf("tipo", "kind", "tipo_producto");
        var unit = table.IndexOf("unidad", "unit_code", "unidad_medida");
        var sunat = table.IndexOf("codigo_sunat", "sunat_product_code", "codigo_producto_sunat");
        var category = table.IndexOf("categoria", "category");

        var rows = new List<ImportRowResult>();
        var ready = new List<(int Line, ProductDetails Details)>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in table.Rows)
        {
            var cells = row.Cells;
            var internalCode = cells[code];
            var key = internalCode.Length == 0 ? null : internalCode;
            if (row.HasExtraCells)
            {
                rows.Add(new ImportRowResult(row.Line, ImportRowStatus.Invalid, key, "La fila tiene más columnas que el encabezado; revise que los textos con comas estén entre comillas."));
                continue;
            }

            var kindCell = kind < 0 ? string.Empty : cells[kind];
            ProductKind parsedKind = ProductKind.Goods;
            if (kindCell.Length > 0 && !KindNames.TryGetValue(CsvTable.Normalize(kindCell), out parsedKind))
            {
                rows.Add(new ImportRowResult(row.Line, ImportRowStatus.Invalid, key, "El tipo no se reconoce: use «bien» o «servicio»."));
                continue;
            }

            if (!TryParseValue(cells[value], table.Delimiter, out var unitValue))
            {
                rows.Add(new ImportRowResult(row.Line, ImportRowStatus.Invalid, key, "El valor unitario no es un número (use punto decimal, por ejemplo 12.50)."));
                continue;
            }

            // Without a unit the convention of the platform is used (the one of the examples of the manual creation): «NIU» for goods and «ZZ» for services. It is not checked against a SUNAT catalogue.
            var unitCell = unit < 0 ? string.Empty : cells[unit];
            var unitCode = unitCell.Length > 0 ? unitCell : parsedKind == ProductKind.Service ? "ZZ" : "NIU";
            var details = new ProductDetails(internalCode, cells[description], parsedKind, unitCode, unitValue, cells[affectation], Pick(cells, sunat), Pick(cells, category));
            if (await Validate(details, cancellationToken) is { } invalid)
            {
                rows.Add(new ImportRowResult(row.Line, ImportRowStatus.Invalid, key, invalid.Detail));
                continue;
            }

            var identity = internalCode.Trim();
            if (seen.TryGetValue(identity, out var firstLine))
            {
                rows.Add(new ImportRowResult(row.Line, ImportRowStatus.Invalid, identity, $"El código se repite: ya está en la línea {firstLine}."));
                continue;
            }

            seen[identity] = row.Line;
            ready.Add((row.Line, details));
        }

        var codes = ready.Select(r => r.Details.InternalCode.Trim()).ToList();
        var existing = codes.Count == 0
            ? []
            : (await db.Products.AsNoTracking().Where(p => codes.Contains(p.InternalCode)).Select(p => p.InternalCode).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var toCreate = new List<(int Line, ProductDetails Details)>();
        foreach (var item in ready)
        {
            var identity = item.Details.InternalCode.Trim();
            if (existing.Contains(identity))
            {
                rows.Add(new ImportRowResult(item.Line, ImportRowStatus.Existing, identity, "Ya existe un producto con ese código; no se modifica."));
            }
            else
            {
                toCreate.Add(item);
            }
        }

        var created = request.Commit;
        if (created && toCreate.Count > 0)
        {
            var now = clock.GetUtcNow();
            foreach (var item in toCreate)
            {
                db.Products.Add(Product.Create(Guid.CreateVersion7(), tenant.Value, item.Details, now));
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                return Error.Conflict(ErrorCodes.ImportConflict, "Importación en conflicto", "Otro proceso creó productos con los mismos códigos mientras se importaba. No se guardó nada; revise y repita.");
            }

            await audit.RecordAsync(new AuditEvent(AuditActions.ProductsImported, "product", null, tenant.Value, NewValues: new Dictionary<string, object?>
            {
                ["created"] = toCreate.Count,
                ["existing"] = rows.Count(r => r.Status == ImportRowStatus.Existing),
                ["invalid"] = rows.Count(r => r.Status == ImportRowStatus.Invalid),
                ["fileSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Csv!)))[..16],
            }), cancellationToken);
        }

        var status = created ? ImportRowStatus.Created : ImportRowStatus.Ready;
        rows.AddRange(toCreate.Select(item => new ImportRowResult(item.Line, status, item.Details.InternalCode.Trim(), null)));
        return ImportResult.Of(created, [.. rows.OrderBy(r => r.Line)]);
    }

    private static string? Pick(IReadOnlyList<string> cells, int index) => index < 0 || cells[index].Length == 0 ? null : cells[index];

    /// <summary>A decimal with a point. With «;» as the delimiter (Excel in Spanish) a lone comma is also accepted as the decimal separator, since there it cannot separate columns.</summary>
    private static bool TryParseValue(string text, char delimiter, out decimal value)
    {
        var clean = text.Trim();
        if (delimiter == ';' && clean.Contains(',', StringComparison.Ordinal) && !clean.Contains('.', StringComparison.Ordinal))
        {
            clean = clean.Replace(',', '.');
        }

        return decimal.TryParse(clean, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }
}
