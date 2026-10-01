using SecureFact.SharedKernel.Results;

namespace SecureFact.SharedKernel.Domain;

/// <summary>Sequential document number: 1 to 8 digits, starting at 1 (SUNAT Programmer Manual).</summary>
public readonly record struct DocumentNumber
{
    public const long MaxValue = 99_999_999;

    private DocumentNumber(long value) => Value = value;

    public long Value { get; }

    public static Result<DocumentNumber> Create(long value)
    {
        if (value is < 1 or > MaxValue)
        {
            return Error.Validation(ErrorCodes.InvalidDocumentNumber, "Número de documento inválido", $"El correlativo debe estar entre 1 y {MaxValue}.");
        }

        return new DocumentNumber(value);
    }

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
