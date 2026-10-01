using System.Text.RegularExpressions;

namespace SecureFact.SharedKernel.Domain;

/// <summary>
/// Structural rules for the identity types of SUNAT catalogue No. 06 (buyer or customer identification). Whether a person exists
/// or a DNI is genuine is not decided here: that would need an official query adapter.
/// </summary>
public static partial class IdentityDocuments
{
    public const string NoDocument = "0";
    public const string Dni = "1";
    public const string ForeignerCard = "4";
    public const string Ruc = "6";
    public const string Passport = "7";
    public const string DiplomaticId = "A";

    /// <summary>Catalogue 06 codes the platform accepts today.</summary>
    public static IReadOnlyList<string> SupportedTypes { get; } = [NoDocument, Dni, ForeignerCard, Ruc, Passport, DiplomaticId];

    [GeneratedRegex(@"^\d{8}$")]
    private static partial Regex DniPattern();

    [GeneratedRegex("^[0-9A-Z]{1,15}$")]
    private static partial Regex Alphanumeric15();

    /// <returns>A human-readable problem, or null when the number is structurally valid for the type.</returns>
    public static string? Validate(string? typeCode, string? number)
    {
        var type = typeCode?.Trim() ?? string.Empty;
        var value = number?.Trim() ?? string.Empty;

        if (!SupportedTypes.Contains(type, StringComparer.Ordinal))
        {
            return $"El tipo de documento de identidad '{type}' no es válido (catálogo 06).";
        }

        return type switch
        {
            Ruc when !SharedKernel.Domain.Ruc.Create(value).IsSuccess => "El RUC no es válido.",
            Dni when !DniPattern().IsMatch(value) => "El DNI debe tener 8 dígitos.",
            ForeignerCard or Passport or DiplomaticId when !Alphanumeric15().IsMatch(value.ToUpperInvariant()) => "El número de documento debe ser alfanumérico de hasta 15 caracteres.",
            NoDocument when value.Length is 0 or > 15 => "Indique el número de documento (o un guion) para personas sin documento.",
            _ => null,
        };
    }
}
