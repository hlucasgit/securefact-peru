using System.Globalization;
using System.Text;

namespace SecureFact.SharedKernel.Export;

/// <summary>
/// What a module hands over when an account takes its data with it (ADR-074). Each module writes only the tables it owns, through the scope of the request: Row Level Security shows it the
/// rows of the account and nothing else. A contributor never writes a secret (password hashes, second-factor seeds, private keys, certificates, signing secrets).
/// </summary>
public interface IAccountDataContributor
{
    Task WriteAsync(IAccountDataWriter writer, CancellationToken cancellationToken);
}

/// <summary>The package of an account. One file at a time: the file of the previous call is complete before the next one starts.</summary>
public interface IAccountDataWriter
{
    /// <summary>Writes <c>datos/&lt;name&gt;.csv</c>. Every row has one cell per column. The rows are read as they are written, so a table of any size is never held in memory.</summary>
    Task WriteTableAsync(string name, IReadOnlyList<string> columns, IAsyncEnumerable<IReadOnlyList<string?>> rows, CancellationToken cancellationToken);

    /// <summary>Writes a file as it is (a signed XML, a CDR). <paramref name="path"/> is relative to the package and never leaves it.</summary>
    Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}

/// <summary>RFC 4180 CSV for a package that people open in a spreadsheet.</summary>
public static class AccountCsv
{
    /// <summary>The byte order mark that makes Excel read the file as UTF-8.</summary>
    public static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];

    public static string Row(IEnumerable<string?> cells) => string.Join(',', cells.Select(Cell)) + "\r\n";

    /// <summary>
    /// A text that a spreadsheet would take for a formula (it starts with =, +, - or @, or with a tab or a carriage return) is written as text, so a file opened in Excel never runs what a customer
    /// or a user typed. Commas, quotes and line breaks are escaped.
    /// </summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return text.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    public static string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string? Stamp(DateTimeOffset? value) => value is null ? null : Stamp(value.Value);

    public static string Day(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string? Day(DateOnly? value) => value is null ? null : Day(value.Value);

    public static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    public static string? Number(decimal? value) => value is null ? null : Number(value.Value);

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string YesNo(bool value) => value ? "si" : "no";
}
