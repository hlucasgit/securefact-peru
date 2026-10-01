using SecureFact.SharedKernel.Results;

namespace SecureFact.SharedKernel.Domain;

/// <summary>
/// Peruvian taxpayer number. Structural validation only: 11 digits plus the modulo-11 check digit.
/// Whether the RUC exists, is active or is "habido" is a SUNAT-side fact and is never decided here.
/// </summary>
public readonly record struct Ruc
{
    private static readonly int[] Weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];

    private Ruc(string value) => Value = value;

    public string Value { get; }

    public static Result<Ruc> Create(string? input)
    {
        var value = input?.Trim();
        if (value is not { Length: 11 } || !value.All(char.IsAsciiDigit))
        {
            return Error.Validation(ErrorCodes.InvalidRuc, "RUC inválido", "El RUC debe tener exactamente 11 dígitos numéricos.");
        }

        if (ComputeCheckDigit(value) != value[10] - '0')
        {
            return Error.Validation(ErrorCodes.InvalidRuc, "RUC inválido", "El dígito verificador del RUC no coincide.");
        }

        return new Ruc(value);
    }

    public override string ToString() => Value ?? string.Empty;

    private static int ComputeCheckDigit(string value)
    {
        var sum = 0;
        for (var i = 0; i < Weights.Length; i++)
        {
            sum += (value[i] - '0') * Weights[i];
        }

        return (11 - (sum % 11)) % 10;
    }
}
