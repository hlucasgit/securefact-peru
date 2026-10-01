using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExcelDataReader;

namespace SecureFact.CatalogImporter;

public sealed record CatalogEntrySeed(string Code, string Description, IReadOnlyDictionary<string, string> Extra);

public sealed record CatalogSeed(string Number, string Name, IReadOnlyList<string> Headers, IReadOnlyList<CatalogEntrySeed> Entries);

public sealed record CatalogSource(string File, long Bytes, string Sha256, string Sheet);

public sealed record CatalogSeedFile(CatalogSource Source, IReadOnlyList<CatalogSeed> Catalogs);

/// <summary>
/// Reads the "Catálogos" sheet of SUNAT's validation-rules workbook (Anexo N.°8). Layout, observed in the 2026-08-26 workbook:
/// a row <c>No. | NN</c> announces the number of the next catalogue; a row <c>Catálogo | &lt;name&gt;</c> starts it; the next row holds
/// column headers (the first two are code and description); entries follow until an empty row or the next marker.
/// </summary>
public static partial class CatalogWorkbookParser
{
    public const string SheetName = "Catálogos";
    private const int MaxColumns = 12;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex(@"^\d{1,3}$")]
    private static partial Regex NumberPattern();

    public static CatalogSeedFile Parse(string xlsxPath)
    {
        // ExcelDataReader needs legacy code pages for .xls support even when reading .xlsx.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        var bytes = File.ReadAllBytes(xlsxPath);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var stream = new MemoryStream(bytes);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var rows = ReadSheet(reader);

        var catalogs = new List<CatalogSeed>();
        string? pendingNumber = null;
        string? name = null;
        string? number = null;
        List<string>? headers = null;
        List<CatalogEntrySeed>? entries = null;

        void Flush()
        {
            if (name is not null && number is not null && headers is not null && entries is { Count: > 0 })
            {
                catalogs.Add(new CatalogSeed(number, name, headers, entries));
            }

            name = null;
            number = null;
            headers = null;
            entries = null;
        }

        foreach (var row in rows)
        {
            var first = row.Length > 0 ? row[0] : string.Empty;
            if (first == "No." && row.Length > 1 && NumberPattern().IsMatch(row[1]))
            {
                Flush();
                pendingNumber = row[1].PadLeft(2, '0');
                continue;
            }

            if (first.StartsWith("Catálogo", StringComparison.Ordinal) && row.Skip(1).Any(c => c.Length > 0) && pendingNumber is not null)
            {
                Flush();
                name = string.Join(" ", row.Skip(1).Where(c => c.Length > 0));
                number = pendingNumber;
                pendingNumber = null;
                continue;
            }

            if (name is null)
            {
                continue;
            }

            if (row.All(c => c.Length == 0))
            {
                if (entries is { Count: > 0 })
                {
                    Flush();
                }

                continue;
            }

            if (headers is null)
            {
                headers = row.TakeWhile(c => c.Length > 0).ToList();
                entries = [];
                continue;
            }

            var code = row.Length > 0 ? row[0] : string.Empty;
            if (code.Length == 0)
            {
                continue;
            }

            var description = row.Length > 1 ? row[1] : string.Empty;
            var extra = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 2; i < Math.Min(headers.Count, row.Length); i++)
            {
                if (row[i].Length > 0)
                {
                    extra[headers[i]] = row[i];
                }
            }

            entries!.Add(new CatalogEntrySeed(code, description, extra));
        }

        Flush();
        return new CatalogSeedFile(new CatalogSource(Path.GetFileName(xlsxPath), bytes.Length, sha, SheetName), catalogs);
    }

    /// <summary>Always LF so the seed is byte-identical on every platform.</summary>
    public static string ToJson(CatalogSeedFile file) => JsonSerializer.Serialize(file, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static List<string[]> ReadSheet(IExcelDataReader reader)
    {
        do
        {
            if (!string.Equals(reader.Name, SheetName, StringComparison.Ordinal))
            {
                continue;
            }

            var rows = new List<string[]>();
            while (reader.Read())
            {
                var cells = new string[MaxColumns];
                for (var i = 0; i < MaxColumns; i++)
                {
                    cells[i] = Clean(i < reader.FieldCount ? reader.GetValue(i) : null);
                }

                rows.Add(cells);
            }

            return rows;
        }
        while (reader.NextResult());

        throw new InvalidDataException($"Sheet '{SheetName}' not found in the workbook.");
    }

    private static string Clean(object? value) => value switch
    {
        null => string.Empty,
        double d => d.ToString("0.#########", CultureInfo.InvariantCulture),
        _ => Regex.Replace(value.ToString() ?? string.Empty, @"\s+", " ").Trim(),
    };
}
