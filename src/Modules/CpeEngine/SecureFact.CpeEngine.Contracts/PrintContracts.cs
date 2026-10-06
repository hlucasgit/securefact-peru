using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine.Contracts;

/// <param name="UnitCode">Catalogue 03 unit code; <c>NIU</c> and <c>ZZ</c> need not be printed (the rule says so).</param>
public sealed record PrintedLine(
    string? UnitCode,
    decimal Quantity,
    string Description,
    decimal UnitValue,
    decimal? UnitPriceIncludingTaxes,
    decimal LineExtensionAmount,
    decimal TaxAmount);

public sealed record PrintedTotals(
    decimal TaxedAmount,
    decimal ExemptAmount,
    decimal UnaffectedAmount,
    decimal FreeAmount,
    decimal IgvAmount,
    decimal TotalAmount,
    decimal OtherDiscounts = 0m,
    decimal OtherCharges = 0m,
    decimal IvapAmount = 0m,
    decimal ExportAmount = 0m,
    decimal IscAmount = 0m,
    decimal IcbperAmount = 0m);

/// <summary>What a printed credit or debit note modifies: the denomination and number of the document, and the reason.</summary>
public sealed record PrintedNote(string ReferencedDocument, string Reason);

/// <summary>One installment of a credit sale as printed: its number (1-based), the due date and the amount.</summary>
public sealed record PrintedInstallment(int Number, DateOnly DueDate, decimal Amount);

/// <param name="BuyerDocumentTypeName">Denomination of the catalogue 06 type (the printed form replaces the code by its name).</param>
/// <param name="QrPayload">Text of the QR (see <see cref="IQrPayloadGenerator"/>).</param>
/// <param name="DigestValue">Base64 <c>ds:DigestValue</c> of the signed XML, printed as the document's summary value.</param>
/// <param name="Installments">Present for a sale on credit: the payment form is then printed as credit followed by the installments.</param>
/// <param name="InitialPayment">Part of a credit sale paid on the issue date; printed in the credit block when there is one.</param>
/// <param name="AdditionalInformation">Extra lines printed before the totals (the detraction and the withholding of the invoice).</param>
/// <param name="Voided">True when SUNAT accepted the document's voiding: every page then carries the word ANULADO (the content and the QR are not altered).</param>
public sealed record PrintedDocument(
    string DocumentTypeCode,
    string Series,
    long Number,
    DateOnly IssueDate,
    string Currency,
    string IssuerName,
    string? IssuerTradeName,
    string IssuerRuc,
    string IssuerAddress,
    string? BuyerDocumentTypeName,
    string? BuyerDocumentNumber,
    string? BuyerName,
    string? BuyerAddress,
    IReadOnlyList<PrintedLine> Lines,
    PrintedTotals Totals,
    string QrPayload,
    string DigestValue,
    PrintedNote? Note = null,
    bool Voided = false,
    IReadOnlyList<PrintedInstallment>? Installments = null,
    decimal InitialPayment = 0m,
    IReadOnlyList<string>? AdditionalInformation = null);

/// <summary>Renders the printed representation (PDF, A4) of an invoice or receipt. Pure: the same input gives the same bytes.</summary>
public interface IPrintedRepresentationRenderer
{
    Result<byte[]> Render(PrintedDocument document);
}
