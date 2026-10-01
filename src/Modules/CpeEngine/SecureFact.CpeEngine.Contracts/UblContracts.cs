using SecureFact.SharedKernel.Results;
using SecureFact.TaxEngine.Contracts;

namespace SecureFact.CpeEngine.Contracts;

public sealed record UblParty(string DocumentTypeCode, string DocumentNumber, string LegalName, string? TradeName = null, string? EstablishmentCode = null);

public sealed record UblLine(
    int LineNumber,
    string Description,
    string UnitCode,
    string? ProductCode,
    decimal Quantity,
    decimal UnitValue,
    decimal? ReferenceUnitValue,
    string IgvAffectationCode);

/// <summary>
/// Canonical input of the UBL generator. Amounts come from the TaxEngine result, never recomputed here: the XML must say exactly
/// what was calculated and numbered (§17: no XML straight from API DTOs).
/// </summary>
/// <param name="OperationTypeCode">Catalogue 51 code, e.g. <c>0101</c> internal sale.</param>
/// <param name="PaymentForm">Forma de pago of an invoice: only <c>Contado</c> is supported; credit needs installments and is refused (SUNAT errors 3245–3267).</param>
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
    string PaymentForm = "Contado");

/// <summary>An unsigned UBL 2.1 document and the file names SUNAT expects for it.</summary>
public sealed record UblDocument(string Xml, string FileBaseName)
{
    public string XmlFileName => FileBaseName + ".xml";

    public string ZipFileName => FileBaseName + ".zip";
}

public interface IUblDocumentGenerator
{
    /// <summary>Generates the unsigned XML for an invoice (01) or receipt (03). Unsupported combinations fail explicitly.</summary>
    Result<UblDocument> GenerateInvoice(UblInvoiceData data);
}
