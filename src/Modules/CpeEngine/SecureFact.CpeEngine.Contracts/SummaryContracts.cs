using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine.Contracts;

/// <summary>
/// One receipt (boleta) line of a daily summary. Amounts are the ones calculated and numbered by Billing, never recomputed here.
/// <see cref="BuyerDocumentTypeCode"/> and <see cref="BuyerDocumentNumber"/> are both null when the receipt has no buyer identification.
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
    decimal IgvRate);

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

    /// <summary>Generates the unsigned daily summary of receipts (all lines with status 1, "adicionar"). Unsupported cases fail explicitly.</summary>
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
    /// Builds, signs and stores the daily summary of the receipts a company issued on <paramref name="referenceDate"/> that no active summary
    /// reports yet (preparing their electronic documents first). More than 500 receipts produce several summaries with consecutive correlatives.
    /// </summary>
    Task<Result<IReadOnlyList<SummaryDto>>> CreateAsync(Guid companyId, DateOnly referenceDate, CancellationToken cancellationToken);

    Task<Result<SummaryDto>> GetAsync(Guid summaryDocumentId, CancellationToken cancellationToken);
}
