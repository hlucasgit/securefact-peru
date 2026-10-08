using System.Globalization;
using System.Text;
using SecureFact.SharedKernel.Results;

namespace SecureFact.SharedKernel.Import;

/// <param name="Line">The line of the file where the record starts.</param>
/// <param name="Cells">The cells of the record, trimmed; padded with empty cells up to the number of headers.</param>
/// <param name="HasExtraCells">The record has more cells than the header: most likely a comma inside a field without quotes, which shifts every column after it.</param>
public sealed record CsvRow(int Line, IReadOnlyList<string> Cells, bool HasExtraCells);

/// <summary>A CSV read into a header and records. Headers are matched by <see cref="CsvTable.Normalize"/>, so «Razón social», «razon_social» and «RAZON SOCIAL» are the same column.</summary>
public sealed class CsvTable
{
    private readonly Dictionary<string, int> _columns = [];

    public CsvTable(IReadOnlyList<string> headers, IReadOnlyList<CsvRow> rows, char delimiter)
    {
        ArgumentNullException.ThrowIfNull(headers);
        Headers = headers;
        Rows = rows;
        Delimiter = delimiter;
        for (var index = 0; index < headers.Count; index++)
        {
            _columns.TryAdd(Normalize(headers[index]), index);
        }
    }

    public IReadOnlyList<string> Headers { get; }

    public IReadOnlyList<CsvRow> Rows { get; }

    public char Delimiter { get; }

    /// <summary>The index of the first column whose name is any of the aliases, or -1.</summary>
    public int IndexOf(params string[] aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        foreach (var alias in aliases)
        {
            if (_columns.TryGetValue(Normalize(alias), out var index))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Lower case, no accents, anything that is not a letter or digit becomes one underscore.</summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length);
        var pendingSeparator = false;
        foreach (var character in name.Trim().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(character);
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('_');
                }

                builder.Append(lower);
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return builder.ToString();
    }
}

/// <summary>Reads the CSV that Excel and the accounting programs produce (RFC 4180 with quotes and line breaks inside quotes) and picks the delimiter itself: «,», «;» or a tab.</summary>
public static class CsvParser
{
    public static Result<CsvTable> Parse(string? text, int maxRows = ImportLimits.MaxRows)
    {
        static Error Bad(string detail) => Error.Validation(ErrorCodes.ImportFileInvalid, "Archivo inválido", detail);

        if (string.IsNullOrWhiteSpace(text))
        {
            return Bad("El archivo está vacío.");
        }

        if (text.Length > ImportLimits.MaxCharacters)
        {
            return Bad($"El archivo es demasiado grande (máximo {ImportLimits.MaxCharacters / 1000} mil caracteres).");
        }

        var content = text.TrimStart('﻿');
        var delimiter = DetectDelimiter(content);
        var records = new List<(int Line, List<string> Cells)>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var quoteLine = 0;
        var line = 1;
        var recordLine = 1;
        var wasQuoted = false;

        void EndCell()
        {
            cells.Add(wasQuoted ? cell.ToString() : cell.ToString().Trim());
            cell.Clear();
            wasQuoted = false;
        }

        void EndRecord()
        {
            EndCell();
            if (cells.Any(c => c.Length > 0))
            {
                records.Add((recordLine, cells));
            }

            cells = [];
        }

        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < content.Length && content[index + 1] == '"')
                    {
                        cell.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (character == '\n')
                    {
                        line++;
                    }

                    cell.Append(character);
                }

                continue;
            }

            if (character == '"' && cell.ToString().Trim().Length == 0)
            {
                inQuotes = true;
                wasQuoted = true;
                quoteLine = line;
                cell.Clear();
            }
            else if (character == delimiter)
            {
                EndCell();
            }
            else if (character == '\n' || character == '\r')
            {
                if (character == '\r' && index + 1 < content.Length && content[index + 1] == '\n')
                {
                    index++;
                }

                EndRecord();
                line++;
                recordLine = line;
            }
            else
            {
                cell.Append(character);
            }
        }

        if (inQuotes)
        {
            return Bad($"Una comilla abierta en la línea {quoteLine} no se cierra.");
        }

        if (cell.Length > 0 || cells.Count > 0)
        {
            EndRecord();
        }

        if (records.Count == 0)
        {
            return Bad("El archivo no tiene encabezado.");
        }

        var headers = records[0].Cells;
        if (records.Count - 1 > maxRows)
        {
            return Bad($"El archivo tiene {records.Count - 1} filas; el máximo es {maxRows}. Divídalo en varios.");
        }

        var rows = records.Skip(1)
            .Select(record => new CsvRow(record.Line, Pad(record.Cells, headers.Count), record.Cells.Count > headers.Count && record.Cells.Skip(headers.Count).Any(c => c.Length > 0)))
            .ToList();
        return new CsvTable(headers, rows, delimiter);
    }

    private static List<string> Pad(List<string> cells, int width)
    {
        var padded = new List<string>(cells);
        while (padded.Count < width)
        {
            padded.Add(string.Empty);
        }

        return padded;
    }

    /// <summary>The most frequent of «,», «;» and tab in the header line, outside quotes. Excel in Spanish saves with «;».</summary>
    private static char DetectDelimiter(string content)
    {
        int commas = 0, semicolons = 0, tabs = 0;
        var inQuotes = false;
        foreach (var character in content)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (inQuotes)
            {
                continue;
            }
            else if (character is '\n' or '\r')
            {
                break;
            }
            else if (character == ',')
            {
                commas++;
            }
            else if (character == ';')
            {
                semicolons++;
            }
            else if (character == '\t')
            {
                tabs++;
            }
        }

        if (commas == 0 && semicolons == 0 && tabs == 0)
        {
            return ',';
        }

        // A tie goes to the comma, then to the semicolon.
        if (commas >= semicolons && commas >= tabs)
        {
            return ',';
        }

        return semicolons >= tabs ? ';' : '\t';
    }
}
