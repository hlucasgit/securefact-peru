using SecureFact.SharedKernel.Results;
using SecureFact.TaxEngine.Contracts;

namespace SecureFact.Billing.Contracts;

/// <summary>SUNAT catalogue No. 01 codes handled by this module. Notes (07/08) arrive with the CDR-aware flow in Phase 4.</summary>
public static class DocumentTypes
{
    public const string Invoice = "01";
    public const string Receipt = "03";
    public const string CreditNote = "07";
    public const string DebitNote = "08";
}

public sealed record CreateSeriesRequest(Guid CompanyId, string DocumentTypeCode, string Code, Guid? EstablishmentId = null);

public sealed record SeriesDto(
    Guid Id,
    Guid TenantId,
    Guid CompanyId,
    Guid? EstablishmentId,
    string DocumentTypeCode,
    string Code,
    long LastNumber,
    bool IsActive,
    DateTimeOffset CreatedAt);

public interface ISeriesAdministration
{
    Task<Result<SeriesDto>> CreateAsync(CreateSeriesRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesDto>> ListAsync(Guid companyId, CancellationToken cancellationToken);

    /// <summary>A deactivated series is kept: issued numbers must stay traceable. It simply stops issuing.</summary>
    Task<Result<Unit>> DeactivateAsync(Guid seriesId, CancellationToken cancellationToken);
}

/// <summary>Buyer identity as it appears on the document (a snapshot: later edits to a customer never alter an issued document).</summary>
/// <param name="DocumentTypeCode">SUNAT catalogue No. 06 code: 6 RUC, 1 DNI, 4 carné de extranjería, 7 pasaporte, A cédula diplomática, 0 sin documento.</param>
public sealed record BuyerSnapshot(string DocumentTypeCode, string DocumentNumber, string Name, string? Address = null, string? Email = null);

public sealed record DocumentLineRequest(
    string Description,
    string UnitCode,
    TaxableLine Tax,
    string? ProductCode = null);

/// <summary>
/// Tax rates are never accepted from the client: the platform resolves them from versioned rules at the issue date.
/// The buyer is given either inline (<paramref name="Buyer"/>) or by reference (<paramref name="CustomerId"/>), never both; a referenced
/// customer is copied into the document as a snapshot.
/// </summary>
public sealed record CreateDocumentRequest(
    Guid SeriesId,
    DateOnly IssueDate,
    string Currency,
    BuyerSnapshot? Buyer,
    IReadOnlyList<DocumentLineRequest> Lines,
    GlobalAdjustments? Adjustments = null,
    Guid? CustomerId = null);

public enum DocumentStatus
{
    /// <summary>Numbered and fully calculated; no electronic artefact exists yet (Phase 3 generates the XML).</summary>
    Validated,
}

public sealed record DocumentLineDto(int LineNumber, string Description, string UnitCode, string? ProductCode, decimal Quantity, decimal LineExtensionAmount, string TaxCode, decimal TotalTaxAmount, decimal? UnitPriceIncludingTaxes, decimal UnitValue = 0m, string IgvAffectationCode = "");

public sealed record DocumentDto(
    Guid Id,
    Guid TenantId,
    Guid CompanyId,
    string DocumentTypeCode,
    string Series,
    long Number,
    DateOnly IssueDate,
    string Currency,
    BuyerSnapshot Buyer,
    DocumentStatus Status,
    IReadOnlyList<DocumentLineDto> Lines,
    TaxCalculationResult Totals,
    DateTimeOffset CreatedAt)
{
    public string FullNumber => $"{Series}-{Number}";
}

public interface IDocumentService
{
    /// <summary>
    /// Creates and numbers a document. Idempotent per <paramref name="idempotencyKey"/>: the same key with the same content returns the
    /// original document; the same key with different content is a conflict.
    /// </summary>
    Task<Result<DocumentDto>> CreateAsync(string idempotencyKey, CreateDocumentRequest request, CancellationToken cancellationToken);

    Task<Result<DocumentDto>> GetAsync(Guid documentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DocumentDto>> ListAsync(Guid? companyId, int skip, int take, CancellationToken cancellationToken);

    /// <summary>Documents of one company, type and issue date, ordered by series and number (for daily summaries).</summary>
    Task<IReadOnlyList<DocumentDto>> ListIssuedAsync(Guid companyId, string documentTypeCode, DateOnly issueDate, int skip, int take, CancellationToken cancellationToken);
}

/// <summary>Integration event types published by Billing through its transactional outbox.</summary>
public static class BillingEvents
{
    /// <summary>A document was numbered and stored. Payload: <see cref="DocumentIssuedEvent"/> (JSON, web casing).</summary>
    public const string DocumentIssued = "billing.document.issued";
}

public sealed record DocumentIssuedEvent(Guid TenantId, Guid DocumentId, Guid CompanyId, string DocumentTypeCode, string Series, long Number);
