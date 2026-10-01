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
    /// <summary>Maximum number of lines SUNAT accepts in one summary file (Programmer Manual, Annex 4).</summary>
    const int MaxLines = 500;

    /// <summary>Generates the unsigned daily summary of receipts (all lines with status 1, "adicionar"). Unsupported cases fail explicitly.</summary>
    Result<SummaryDocument> Generate(SummaryData data);
}
