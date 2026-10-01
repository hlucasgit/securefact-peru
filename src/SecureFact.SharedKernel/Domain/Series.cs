using SecureFact.SharedKernel.Results;

namespace SecureFact.SharedKernel.Domain;

/// <summary>
/// Document series: four uppercase alphanumeric characters (SUNAT Programmer Manual, file naming section).
/// Which prefix letter applies to which document type (e.g. F/B) is a versioned rule owned by Billing, not enforced here.
/// </summary>
public readonly record struct Series
{
    private Series(string value) => Value = value;

    public string Value { get; }

    public static Result<Series> Create(string? input)
    {
        var value = input?.Trim().ToUpperInvariant();
        if (value is not { Length: 4 } || !value.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)))
        {
            return Error.Validation(ErrorCodes.InvalidSeries, "Serie inválida", "La serie debe tener 4 caracteres alfanuméricos.");
        }

        return new Series(value);
    }

    public override string ToString() => Value ?? string.Empty;
}
