using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Customers.Contracts;
using SecureFact.Customers.Domain;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Import;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Customers.Application;

internal sealed partial class CustomerAdministration
{
    /// <summary>The names people write in a spreadsheet for the codes of catalogue 06 (compared without accents or case).</summary>
    private static readonly Dictionary<string, string> DocumentTypeNames = new()
    {
        ["ruc"] = "6",
        ["dni"] = "1",
        ["ce"] = "4",
        ["carnet_de_extranjeria"] = "4",
        ["carnet_extranjeria"] = "4",
        ["pasaporte"] = "7",
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
        var number = table.IndexOf("numero_documento", "documento", "numero", "nro_documento", "document_number", "ruc", "dni");
        var name = table.IndexOf("nombre", "razon_social", "nombre_o_razon_social", "name");
        if (number < 0 || name < 0)
        {
            return Error.Validation(ErrorCodes.ImportFileInvalid, "Archivo inválido", "Faltan columnas obligatorias: «numero_documento» y «nombre» (o «razon_social»). El encabezado debe estar en la primera línea.");
        }

        var type = table.IndexOf("tipo_documento", "tipo", "document_type", "tipo_de_documento");
        var address = table.IndexOf("direccion", "address");
        var email = table.IndexOf("correo", "email", "correo_electronico");
        var phone = table.IndexOf("telefono", "phone", "celular");

        var rows = new List<ImportRowResult>();
        var ready = new List<(int Line, CustomerDetails Details)>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in table.Rows)
        {
            var cells = row.Cells;
            var rawNumber = cells[number];
            var key = rawNumber.Length == 0 ? null : rawNumber;
            if (row.HasExtraCells)
            {
                rows.Add(new ImportRowResult(row.Line, ImportRowStatus.Invalid, key, "La fila tiene más columnas que el encabezado; revise que los textos con comas estén entre comillas."));
                continue;
            }

            var typeCode = TypeOf(type < 0 ? null : cells[type], rawNumber);
            if (typeCode is null)
            {
                rows.Add(new ImportRowResult(row.Line, ImportRowStatus.Invalid, key, type < 0
                    ? "No se pudo deducir el tipo de documento: sin columna «tipo_documento», solo 11 dígitos (RUC) u 8 dígitos (DNI) se reconocen."
                    : "El tipo de documento no se reconoce: use el código del catálogo 06 (6, 1, 4, 7, A, 0) o RUC, DNI, CE, PASAPORTE."));
                continue;
            }

            var details = new CustomerDetails(typeCode, rawNumber, cells[name], Pick(cells, address), Pick(cells, email), Pick(cells, phone));
            if (await Validate(details, cancellationToken) is { } invalid)
            {
                rows.Add(new ImportRowResult(row.Line, ImportRowStatus.Invalid, $"{typeCode}-{Customer.Normalize(rawNumber)}", invalid.Detail));
                continue;
            }

            var identity = $"{typeCode}-{Customer.Normalize(rawNumber)}";
            if (seen.TryGetValue(identity, out var firstLine))
            {
                rows.Add(new ImportRowResult(row.Line, ImportRowStatus.Invalid, identity, $"El documento se repite: ya está en la línea {firstLine}."));
                continue;
            }

            seen[identity] = row.Line;
            ready.Add((row.Line, details));
        }

        var existing = await ExistingAsync(ready.Select(r => Customer.Normalize(r.Details.DocumentNumber)).ToHashSet(StringComparer.Ordinal), cancellationToken);
        var toCreate = new List<(int Line, CustomerDetails Details)>();
        foreach (var item in ready)
        {
            var identity = $"{item.Details.DocumentTypeCode.Trim()}-{Customer.Normalize(item.Details.DocumentNumber)}";
            if (existing.Contains(identity))
            {
                rows.Add(new ImportRowResult(item.Line, ImportRowStatus.Existing, identity, "Ya existe un cliente con ese documento; no se modifica."));
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
                db.Customers.Add(Customer.Create(Guid.CreateVersion7(), tenant.Value, item.Details, now));
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                return Error.Conflict(ErrorCodes.ImportConflict, "Importación en conflicto", "Otro proceso creó clientes con los mismos documentos mientras se importaba. No se guardó nada; revise y repita.");
            }

            await audit.RecordAsync(new AuditEvent(AuditActions.CustomersImported, "customer", null, tenant.Value, NewValues: new Dictionary<string, object?>
            {
                ["created"] = toCreate.Count,
                ["existing"] = rows.Count(r => r.Status == ImportRowStatus.Existing),
                ["invalid"] = rows.Count(r => r.Status == ImportRowStatus.Invalid),
                ["fileSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Csv!)))[..16],
            }), cancellationToken);
        }

        var status = created ? ImportRowStatus.Created : ImportRowStatus.Ready;
        rows.AddRange(toCreate.Select(item => new ImportRowResult(item.Line, status, $"{item.Details.DocumentTypeCode.Trim()}-{Customer.Normalize(item.Details.DocumentNumber)}", null)));
        return ImportResult.Of(created, [.. rows.OrderBy(r => r.Line)]);
    }

    private async Task<HashSet<string>> ExistingAsync(HashSet<string> numbers, CancellationToken cancellationToken)
    {
        if (numbers.Count == 0)
        {
            return [];
        }

        var found = await db.Customers.AsNoTracking().Where(c => numbers.Contains(c.DocumentNumber)).Select(c => new { c.DocumentTypeCode, c.DocumentNumber }).ToListAsync(cancellationToken);
        return found.Select(c => $"{c.DocumentTypeCode}-{c.DocumentNumber}").ToHashSet(StringComparer.Ordinal);
    }

    private static string? Pick(IReadOnlyList<string> cells, int index) => index < 0 || cells[index].Length == 0 ? null : cells[index];

    /// <summary>The catalogue 06 code of a cell: the code itself, a common name, or (only when the file has no type column) the one that the length of the number implies.</summary>
    private static string? TypeOf(string? cell, string number)
    {
        if (cell is null)
        {
            var digits = number.Trim();
            return digits.All(char.IsAsciiDigit) ? digits.Length switch { 11 => "6", 8 => "1", _ => null } : null;
        }

        if (cell.Length == 0)
        {
            return null;
        }

        if (DocumentTypeNames.TryGetValue(CsvTable.Normalize(cell), out var code))
        {
            return code;
        }

        return cell.Length == 1 && (char.IsAsciiLetterOrDigit(cell[0])) ? cell.ToUpper(CultureInfo.InvariantCulture) : null;
    }
}
