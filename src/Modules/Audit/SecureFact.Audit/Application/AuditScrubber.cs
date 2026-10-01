using System.Text.Json;
using System.Text.RegularExpressions;

namespace SecureFact.Audit.Application;

/// <summary>
/// Last line of defence against secrets reaching the audit trail: values of well-known sensitive property names are replaced.
/// Output is canonical (keys sorted ordinally) so the same facts always serialise to the same text.
/// </summary>
internal static partial class AuditScrubber
{
    public const string Redacted = "[redacted]";

    // The text is stored and read back as data (never rendered as HTML), so keep accented characters readable.
    private static readonly JsonSerializerOptions Options = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex("pass(word)?|secret|token|api[-_]?key|private[-_]?key|pfx|credential|authorization|cvv|clave|hash|otp|seed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sensitive();

    public static string? ToCanonicalJson(IReadOnlyDictionary<string, object?>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            sorted[key] = Sensitive().IsMatch(key) ? Redacted : Normalize(value);
        }

        return JsonSerializer.Serialize(sorted, Options);
    }

    private static object? Normalize(object? value) => value switch
    {
        null => null,
        Guid g => g.ToString("D"),
        DateTimeOffset d => d.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        IEnumerable<string> list => list.Order(StringComparer.Ordinal).ToArray(),
        string or bool or int or long or decimal => value,
        _ => value.ToString(),
    };
}
