using System.Text.RegularExpressions;
using SecureFact.Billing.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Billing.Application;

/// <summary>
/// Versioned business rules for documents (ADR-008). Each rule carries its source in docs/regulatory/sources.md.
/// The baseline version applies from the beginning of time because no earlier rule set has been reviewed.
/// </summary>
internal static partial class BillingRules
{
    [GeneratedRegex("^F[A-Z0-9]{3}$")]
    private static partial Regex InvoiceSeries();

    [GeneratedRegex("^B[A-Z0-9]{3}$")]
    private static partial Regex ReceiptSeries();

    [GeneratedRegex("^[FB][A-Z0-9]{3}$")]
    private static partial Regex NoteSeries();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();

    [GeneratedRegex("^[0-9A-Z]{1,15}$")]
    private static partial Regex AlphanumericIdentity();

    [GeneratedRegex(@"^\d{8}$")]
    private static partial Regex Dni();

    private static readonly string[] SupportedBuyerDocumentTypes = ["0", "1", "4", "6", "7", "A"];

    /// <summary>
    /// Maximum age of the issue date when the document is created (SEE-del Contribuyente: 3 calendar days, S02; the same value is applied
    /// to receipts until the receipt guide is read, R-017). Source: docs/regulatory/sources.md S02, S16.
    /// </summary>
    public const int MaxIssueDateAgeDays = 3;

    /// <summary>Series format per document type (S16 "General", rule 0151). Numeric contingency series are not supported yet.</summary>
    public static Error? ValidateSeriesCode(string documentTypeCode, string code)
    {
        var ok = documentTypeCode switch
        {
            DocumentTypes.Invoice => InvoiceSeries().IsMatch(code),
            DocumentTypes.Receipt => ReceiptSeries().IsMatch(code),
            DocumentTypes.CreditNote or DocumentTypes.DebitNote => NoteSeries().IsMatch(code),
            _ => false,
        };

        if (documentTypeCode is not (DocumentTypes.Invoice or DocumentTypes.Receipt or DocumentTypes.CreditNote or DocumentTypes.DebitNote))
        {
            return Error.Validation(ErrorCodes.DocumentTypeNotSupported, "Tipo de documento no soportado", $"El tipo de documento '{documentTypeCode}' no está disponible.");
        }

        return ok
            ? null
            : Error.Validation(
                ErrorCodes.InvalidSeriesConfiguration,
                "Serie inválida",
                documentTypeCode switch
                {
                    DocumentTypes.Invoice => "La serie de facturas debe tener 4 caracteres alfanuméricos y empezar con F.",
                    DocumentTypes.Receipt => "La serie de boletas debe tener 4 caracteres alfanuméricos y empezar con B.",
                    _ => "La serie de notas debe tener 4 caracteres alfanuméricos y empezar con F o B.",
                });
    }

    public static Error? ValidateCurrency(string? currency) =>
        currency is not null && CurrencyCode().IsMatch(currency)
            ? null
            : Error.Validation(ErrorCodes.InvalidDocument, "Moneda inválida", "La moneda debe ser un código de 3 letras mayúsculas (catálogo 02).");

    /// <summary>Buyer identification. Invoices require a RUC (S02); receipts accept the catalogue-06 identity types.</summary>
    public static Error? ValidateBuyer(string documentTypeCode, BuyerSnapshot? buyer)
    {
        static Error Bad(string detail) => Error.Validation(ErrorCodes.InvalidDocument, "Adquirente inválido", detail);

        if (buyer is null || string.IsNullOrWhiteSpace(buyer.Name) || buyer.Name.Trim().Length > 250)
        {
            return Bad("El nombre o razón social del adquirente es obligatorio (máximo 250 caracteres).");
        }

        var type = buyer.DocumentTypeCode?.Trim() ?? string.Empty;
        var number = buyer.DocumentNumber?.Trim() ?? string.Empty;

        if (documentTypeCode == DocumentTypes.Invoice && type != "6")
        {
            return Bad("Las facturas solo se emiten a adquirentes con RUC.");
        }

        if (!SupportedBuyerDocumentTypes.Contains(type, StringComparer.Ordinal))
        {
            return Bad($"El tipo de documento de identidad '{type}' no es válido (catálogo 06).");
        }

        switch (type)
        {
            case "6" when !Ruc.Create(number).IsSuccess:
                return Bad("El RUC del adquirente no es válido.");
            case "1" when !Dni().IsMatch(number):
                return Bad("El DNI debe tener 8 dígitos.");
            case "4" or "7" or "A" when !AlphanumericIdentity().IsMatch(number.ToUpperInvariant()):
                return Bad("El número de documento debe ser alfanumérico de hasta 15 caracteres.");
            case "0" when number.Length is 0 or > 15:
                return Bad("Indique el número de documento (o un guion) para adquirentes sin documento.");
        }

        if (buyer.Address is { Length: > 250 } || buyer.Email is { Length: > 254 })
        {
            return Bad("La dirección o el correo del adquirente exceden la longitud permitida.");
        }

        return null;
    }
}
