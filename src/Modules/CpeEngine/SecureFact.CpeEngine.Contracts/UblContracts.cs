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
    decimal IgvRate);

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
