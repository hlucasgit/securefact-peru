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
    /// <param name="export">An export invoice goes to a buyer abroad: the sheet forbids the RUC there (rule 2800) and takes the other identity types.</param>
    public static Error? ValidateBuyer(string documentTypeCode, BuyerSnapshot? buyer, bool export = false)
    {
        static Error Bad(string detail) => Error.Validation(ErrorCodes.InvalidDocument, "Adquirente inválido", detail);

        if (buyer is null || string.IsNullOrWhiteSpace(buyer.Name) || buyer.Name.Trim().Length > 250)
        {
            return Bad("El nombre o razón social del adquirente es obligatorio (máximo 250 caracteres).");
        }

        if (export)
        {
            if (buyer.DocumentTypeCode?.Trim() is not (IdentityDocuments.NoDocument or IdentityDocuments.ForeignerCard or IdentityDocuments.Passport or IdentityDocuments.DiplomaticId))
            {
                return Bad("Una factura de exportación se emite a un adquirente del exterior: documento de identidad tipo 0, 4, 7 o A, no RUC (regla 2800).");
            }
        }
        else if (documentTypeCode == DocumentTypes.Invoice && buyer.DocumentTypeCode?.Trim() != IdentityDocuments.Ruc)
        {
            return Bad("Las facturas solo se emiten a adquirentes con RUC.");
        }

        if (IdentityDocuments.Validate(buyer.DocumentTypeCode, buyer.DocumentNumber) is { } problem)
        {
            return Bad(problem);
        }

        if (buyer.Address is { Length: > 250 } || buyer.Email is { Length: > 254 })
        {
            return Bad("La dirección o el correo del adquirente exceden la longitud permitida.");
        }

        return null;
    }
}
