using System.Globalization;
using SecureFact.CpeEngine.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine;

/// <summary>
/// Field order and separator from SUNAT "Anexo N.° 6 Aspectos técnicos SEE – Del contribuyente", §6.4.3 (S19 in docs/regulatory/sources.md):
/// <c>RUC | TIPO DE DOCUMENTO | SERIE | NÚMERO | MTO TOTAL IGV | MTO TOTAL DEL COMPROBANTE | FECHA DE EMISIÓN | TIPO DE DOCUMENTO ADQUIRENTE |
/// NÚMERO DE DOCUMENTO ADQUIRENTE | VALOR RESUMEN</c>, separator <c>|</c>. Amounts and date use the same format as the electronic document
/// (two decimals with a dot, <c>YYYY-MM-DD</c>). The norm says buyer data and IGV are included "de ser el caso"; absent values are emitted as
/// empty buyer fields and <c>0.00</c> IGV (assumption R-018, to confirm).
/// </summary>
internal sealed class QrPayloadGenerator : IQrPayloadGenerator
{
    private const char Separator = '|';

    public Result<string> Build(QrData data)
    {
        if (data is null)
        {
            return Bad("Faltan los datos del QR.");
        }

        if (!Ruc.Create(data.IssuerRuc).IsSuccess)
        {
            return Bad("El RUC del emisor no es válido.");
        }

        if (data.TotalIgv < 0 || data.TotalAmount < 0)
        {
            return Bad("Los importes del QR no pueden ser negativos.");
        }

        foreach (var (name, value) in new[]
        {
            ("tipo de documento", data.DocumentTypeCode),
            ("serie", data.Series),
            ("número", data.Number),
            ("tipo de documento del adquirente", data.BuyerDocumentTypeCode ?? string.Empty),
            ("número de documento del adquirente", data.BuyerDocumentNumber ?? string.Empty),
            ("valor resumen", data.DigestValue),
        })
        {
            if (value.Contains(Separator, StringComparison.Ordinal) || value.Any(char.IsControl))
            {
                return Bad($"El campo '{name}' contiene caracteres no permitidos.");
            }
        }

        if (string.IsNullOrWhiteSpace(data.DocumentTypeCode) || string.IsNullOrWhiteSpace(data.Series)
            || string.IsNullOrWhiteSpace(data.Number) || string.IsNullOrWhiteSpace(data.DigestValue))
        {
            return Bad("Tipo de documento, serie, número y valor resumen son obligatorios.");
        }

        if (!IsBase64(data.DigestValue))
        {
            return Bad("El valor resumen debe estar en Base64.");
        }

        return string.Join(
            Separator,
            data.IssuerRuc,
            data.DocumentTypeCode,
            data.Series,
            data.Number,
            Amount(data.TotalIgv),
            Amount(data.TotalAmount),
            data.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            data.BuyerDocumentTypeCode ?? string.Empty,
            data.BuyerDocumentNumber ?? string.Empty,
            data.DigestValue);
    }

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static bool IsBase64(string value)
    {
        Span<byte> buffer = stackalloc byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out _);
    }

    private static Error Bad(string detail) => Error.Validation(ErrorCodes.CpeInvalidQrField, "Datos de QR inválidos", detail);
}
