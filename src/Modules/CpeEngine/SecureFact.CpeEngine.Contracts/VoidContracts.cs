using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine.Contracts;

/// <param name="DocumentTypeCode">01 invoice, 07 or 08 note of an invoice (series F).</param>
/// <param name="Reason">Why the document is voided (3 to 100 characters; shorter draws observation 4203).</param>
public sealed record VoidedLineData(int LineNumber, string DocumentTypeCode, string Series, long Number, string Reason);

/// <param name="ReferenceDate">Issue date of the documents being voided; they must all share it (rule 2375).</param>
/// <param name="IssueDate">Date the communication is generated; it names the file and is never before <paramref name="ReferenceDate"/>.</param>
/// <param name="Correlative">1 to 99999; one per file generated for the same day.</param>
public sealed record VoidedData(string Ruc, string LegalName, DateOnly ReferenceDate, DateOnly IssueDate, int Correlative, IReadOnlyList<VoidedLineData> Lines);

/// <summary>An unsigned voided-documents communication (comunicación de baja, UBL 2.0 VoidedDocuments) and its SUNAT file names.</summary>
public sealed record VoidedDocument(string Xml, string Identifier, string FileBaseName)
{
    public string ZipFileName => FileBaseName + ".zip";
}

public interface IVoidedDocumentsGenerator
{
    /// <summary>Maximum lines per communication this module sends; the same ceiling as daily summaries (assumption R-046).</summary>
    const int MaxLines = 500;

    Result<VoidedDocument> Generate(VoidedData data);
}

/// <param name="DocumentId">The billing document (an invoice, or a note of an invoice) to void.</param>
/// <param name="Reason">3 to 100 characters.</param>
public sealed record VoidItem(Guid DocumentId, string Reason);

public sealed record CreateVoidRequest(Guid CompanyId, IReadOnlyList<VoidItem> Items);

public interface IVoidService
{
    /// <summary>
    /// Builds, signs and stores the voided-documents communications for the given documents: one per issue date (SUNAT wants a single reference
    /// date per file) and at most 500 lines each. Each document must have an accepted CDR, be an invoice or a note of an invoice, not be voided
    /// or in a pending communication already, and be at most 7 days old (rule 2957). Send and follow each communication with the electronic-document
    /// endpoints (<c>send</c>, <c>poll</c>); the document is voided once SUNAT accepts it.
    /// </summary>
    Task<Result<IReadOnlyList<SummaryDto>>> CreateAsync(CreateVoidRequest request, CancellationToken cancellationToken);

    Task<Result<SummaryDto>> GetAsync(Guid voidCommunicationId, CancellationToken cancellationToken);
}
