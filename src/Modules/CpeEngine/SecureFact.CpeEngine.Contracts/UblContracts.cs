using SecureFact.SharedKernel.Results;
using SecureFact.TaxEngine.Contracts;

namespace SecureFact.CpeEngine.Contracts;

/// <summary>One installment of a credit sale: the amount due and its due date.</summary>
public sealed record UblInstallment(decimal Amount, DateOnly DueDate);

public sealed record UblParty(string DocumentTypeCode, string DocumentNumber, string LegalName, string? TradeName = null, string? EstablishmentCode = null);

/// <param name="DiscountAffectingBase">Line discount, code 00 of catalogue 53 (the taxable base is reduced).</param>
/// <param name="ChargeAffectingBase">Line charge, code 47.</param>
/// <param name="DiscountNotAffectingBase">Line discount, code 01 (the base is untouched; the unit price with taxes reflects it).</param>
/// <param name="ChargeNotAffectingBase">Line charge, code 48.</param>
public sealed record UblLine(
    int LineNumber,
    string Description,
    string UnitCode,
    string? ProductCode,
    decimal Quantity,
    decimal UnitValue,
    decimal? ReferenceUnitValue,
    string IgvAffectationCode,
    decimal DiscountAffectingBase = 0m,
    decimal ChargeAffectingBase = 0m,
    decimal DiscountNotAffectingBase = 0m,
    decimal ChargeNotAffectingBase = 0m);

/// <summary>
/// Canonical input of the UBL generator. Amounts come from the TaxEngine result, never recomputed here: the XML must say exactly
/// what was calculated and numbered (§17: no XML straight from API DTOs).
/// </summary>
/// <param name="OperationTypeCode">Catalogue 51 code, e.g. <c>0101</c> internal sale.</param>
/// <param name="PaymentForm">Forma de pago of an invoice: <c>Contado</c>, or <c>Credito</c> together with <paramref name="Installments"/> (SUNAT rules 3244–3267, 3319).</param>
/// <param name="Installments">The installments of a credit sale: at least one, their amounts adding up to the payable amount, each due after the issue date.</param>
/// <param name="Adjustments">Global discounts and charges (catalogue 53 codes 02, 03, 49, 50) exactly as given to the TaxEngine; the generator states them and checks that they agree with <paramref name="Totals"/>.</param>
public sealed record UblInvoiceData(
    string DocumentTypeCode,
    string Series,
    long Number,
    DateOnly IssueDate,
    TimeOnly? IssueTime,
    string Currency,
    string OperationTypeCode,
    UblParty Issuer,
    UblParty Buyer,
    IReadOnlyList<UblLine> Lines,
    TaxCalculationResult Totals,
    decimal IgvRate,
    string PaymentForm = "Contado",
    GlobalAdjustments? Adjustments = null,
    IReadOnlyList<UblInstallment>? Installments = null);

/// <summary>An unsigned UBL 2.1 document and the file names SUNAT expects for it.</summary>
public sealed record UblDocument(string Xml, string FileBaseName)
{
    public string XmlFileName => FileBaseName + ".xml";

    public string ZipFileName => FileBaseName + ".zip";
}

/// <summary>
/// Canonical input of a credit (07) or debit (08) note. The note modifies exactly one invoice (01) or receipt (03); the reason comes from
/// catalogue 09 (credit) or 10 (debit) and is explained in <see cref="ReasonDescription"/> (1-500 characters). A credit note of reason 13 (adjustment of the
/// amounts or dates of the installments) modifies an invoice sold on credit, has a payable amount of zero and states the new <paramref name="Installments"/>.
/// </summary>
public sealed record UblNoteData(
    string DocumentTypeCode,
    string Series,
    long Number,
    DateOnly IssueDate,
    TimeOnly? IssueTime,
    string Currency,
    string ReasonCode,
    string ReasonDescription,
    string ReferencedDocumentTypeCode,
    string ReferencedSeries,
    long ReferencedNumber,
    UblParty Issuer,
    UblParty Buyer,
    IReadOnlyList<UblLine> Lines,
    TaxCalculationResult Totals,
    decimal IgvRate,
    IReadOnlyList<UblInstallment>? Installments = null);

public interface IUblDocumentGenerator
{
    /// <summary>Generates the unsigned XML of a credit or debit note (UBL 2.1 CreditNote / DebitNote). Unsupported combinations fail explicitly.</summary>
    Result<UblDocument> GenerateNote(UblNoteData data);

    /// <summary>Generates the unsigned XML for an invoice (01) or receipt (03). Unsupported combinations fail explicitly.</summary>
    Result<UblDocument> GenerateInvoice(UblInvoiceData data);
}
