using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine.Contracts;

/// <summary>
/// One line of a daily summary: a receipt (03) or a credit/debit note (07/08) that modifies a receipt. Amounts are the ones calculated and numbered by Billing, never recomputed here.
/// <see cref="BuyerDocumentTypeCode"/> and <see cref="BuyerDocumentNumber"/> are both null when the receipt has no buyer identification.
/// <see cref="OtherCharges"/> and <see cref="OtherDiscounts"/> are the charges and discounts that do not affect the taxable base (catalogue 53 codes 48/50 and 01/03):
/// the summary informs the charges in an <c>AllowanceCharge</c> node, and both enter the total. With <see cref="IsIvap"/> the tax of the line is the IVAP
/// (tax 1016) rather than the IGV: <c>TaxedAmount</c>, <c>IgvAmount</c> and <c>IgvRate</c> then hold its base, its amount and its rate. <see cref="ExportAmount"/> is the sale value
/// of an export (catalogue 11 code 04); the IGV of such a receipt is zero.
/// </summary>
public sealed record SummaryLineData(
    int LineNumber,
    string Series,
    long Number,
    string? BuyerDocumentTypeCode,
    string? BuyerDocumentNumber,
    string Currency,
    decimal TotalAmount,
    decimal TaxedAmount,
    decimal ExemptAmount,
    decimal UnaffectedAmount,
    decimal IgvAmount,
    decimal IgvRate,
    string DocumentTypeCode = "03",
    string? ReferencedDocumentTypeCode = null,
    string? ReferencedSeries = null,
    long? ReferencedNumber = null,
    string Status = "1",
    decimal OtherCharges = 0m,
    decimal OtherDiscounts = 0m,
    bool IsIvap = false,
    decimal ExportAmount = 0m);

/// <param name="ReferenceDate">Issue date of every receipt in the summary (they must all share it).</param>
/// <param name="IssueDate">Date the summary is generated; it names the file and is never before <paramref name="ReferenceDate"/>.</param>
/// <param name="Correlative">1 to 99999; one per file generated for the same day (blocks of at most 500 lines each).</param>
public sealed record SummaryData(string Ruc, string LegalName, DateOnly ReferenceDate, DateOnly IssueDate, int Correlative, IReadOnlyList<SummaryLineData> Lines);

/// <summary>An unsigned daily-summary XML (UBL 2.0 SummaryDocuments) and its SUNAT file names.</summary>
public sealed record SummaryDocument(string Xml, string Identifier, string FileBaseName)
{
    public string ZipFileName => FileBaseName + ".zip";
}

public interface ISummaryDocumentGenerator
{
    /// <summary>Maximum number of lines SUNAT accepts in one summary file (Programmer Manual: summaries are sent in blocks of 500 lines).</summary>
    const int MaxLines = 500;

    /// <summary>Generates the unsigned daily summary of receipts and of the notes that modify receipts (all lines with status 1, "adicionar"). Unsupported cases fail explicitly.</summary>
    Result<SummaryDocument> Generate(SummaryData data);
}

/// <summary>A daily summary file and the receipts it reports.</summary>
/// <param name="Document">The electronic document of the summary itself (type <c>RC</c>): state, ticket, CDR.</param>
/// <param name="ElectronicDocumentIds">Receipts reported by this summary, in line order.</param>
public sealed record SummaryDto(ElectronicDocumentDto Document, DateOnly ReferenceDate, IReadOnlyList<Guid> ElectronicDocumentIds);

public sealed record CreateSummaryRequest(Guid CompanyId, DateOnly ReferenceDate);

public interface ISummaryService
{
    /// <summary>
    /// Builds, signs and stores the daily summary of the receipts, and of the notes that modify receipts, a company issued on
    /// <paramref name="referenceDate"/> that no active summary reports yet (preparing their electronic documents first). A note is included only
    /// once the receipt it modifies has an accepted summary; before that it waits. More than 500 receipts produce several summaries with consecutive correlatives.
    /// </summary>
    Task<Result<IReadOnlyList<SummaryDto>>> CreateAsync(Guid companyId, DateOnly referenceDate, CancellationToken cancellationToken);

    Task<Result<SummaryDto>> GetAsync(Guid summaryDocumentId, CancellationToken cancellationToken);
}
