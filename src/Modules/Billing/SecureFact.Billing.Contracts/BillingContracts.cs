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

/// <summary>Catalogue 51 operation types an invoice can be issued with today.</summary>
public static class OperationTypes
{
    /// <summary>0101 – internal sale.</summary>
    public const string Sale = "0101";

    /// <summary>0200 – export of goods (the sale is not taxed: lines with affectation 40, tax 9995).</summary>
    public const string Export = "0200";

    /// <summary>1001 – operation subject to detraction. Implied by a <see cref="Detraction"/> and never given by the client without one.</summary>
    public const string SaleWithDetraction = "1001";
}

/// <summary>
/// Detraction (SPOT) of an invoice: the buyer deposits <paramref name="Amount"/> in the issuer's account at the Banco de la Nación. SUNAT's rules check its structure but not the
/// percentage or the amount, so both are the issuer's data; the platform only checks that they agree. The amount is always in soles.
/// </summary>
/// <param name="GoodsOrServiceCode">Catalogue 54 code (e.g. <c>037</c>, other services taxed with the IGV).</param>
/// <param name="Percentage">As a percentage, e.g. <c>12</c> for 12 %.</param>
/// <param name="AccountNumber">Issuer's account number at the Banco de la Nación.</param>
public sealed record Detraction(string GoodsOrServiceCode, decimal Percentage, decimal Amount, string AccountNumber);

/// <summary>IGV withholding that the buyer, a withholding agent, applies to the invoice: <paramref name="Percentage"/> of the payable amount (e.g. <c>3</c> for 3 %).</summary>
public sealed record RetentionRequest(decimal Percentage);

/// <summary>The IGV withholding as issued: its percentage, the operation amount it applies to (the payable amount) and the amount withheld.</summary>
public sealed record IgvRetention(decimal Percentage, decimal BaseAmount, decimal Amount);

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

/// <summary>One installment (cuota) of an invoice sold on credit: the amount due and the day it falls due.</summary>
public sealed record Installment(decimal Amount, DateOnly DueDate);

/// <summary>
/// Tax rates are never accepted from the client: the platform resolves them from versioned rules at the issue date.
/// The buyer is given either inline (<paramref name="Buyer"/>) or by reference (<paramref name="CustomerId"/>), never both; a referenced
/// customer is copied into the document as a snapshot. An invoice (never a receipt) is sold on credit when <paramref name="Installments"/> is given: its
/// amounts must add up to the payable amount and every due date must fall after the issue date. <paramref name="OperationTypeCode"/> is the catalogue 51 type:
/// <c>0101</c> by default, or <c>0200</c> for the export of goods (invoices only, every line with affectation 40, and a buyer without RUC).
/// <paramref name="InitialPayment"/> is the part of a credit sale paid on the issue date (entrega inicial): the installments then add up to the payable amount minus it.
/// An invoice may carry a <paramref name="Detraction"/> (operation type 1001) or an IGV <paramref name="Retention"/>, never both; the net pending amount of a credit sale then
/// excludes them too.
/// </summary>
public sealed record CreateDocumentRequest(
    Guid SeriesId,
    DateOnly IssueDate,
    string Currency,
    BuyerSnapshot? Buyer,
    IReadOnlyList<DocumentLineRequest> Lines,
    GlobalAdjustments? Adjustments = null,
    Guid? CustomerId = null,
    IReadOnlyList<Installment>? Installments = null,
    string? OperationTypeCode = null,
    decimal? InitialPayment = null,
    Detraction? Detraction = null,
    RetentionRequest? Retention = null);

/// <summary>
/// A credit (07) or debit (08) note. The series decides which; the note modifies one issued invoice or receipt, takes its currency and buyer, and
/// cannot be dated before it. <see cref="ReasonCode"/> is catalogue 09 (credit: 01-10 and 13) or 10 (debit: 01-03); <see cref="Reason"/> explains it.
/// Reason 13 adjusts the installments of an invoice sold on credit: it gives the new <paramref name="Installments"/> and no lines (the note carries a single line worth
/// zero, because nothing is sold or returned); every other reason gives lines and no installments.
/// </summary>
public sealed record CreateNoteRequest(
    Guid SeriesId,
    Guid ReferencedDocumentId,
    DateOnly IssueDate,
    string ReasonCode,
    string Reason,
    IReadOnlyList<DocumentLineRequest>? Lines,
    GlobalAdjustments? Adjustments = null,
    IReadOnlyList<Installment>? Installments = null);

/// <summary>What a note modifies and why.</summary>
public sealed record NoteInfo(string ReasonCode, string Reason, Guid ReferencedDocumentId, string ReferencedDocumentTypeCode, string ReferencedSeries, long ReferencedNumber);

public enum DocumentStatus
{
    /// <summary>Numbered and fully calculated; no electronic artefact exists yet (Phase 3 generates the XML).</summary>
    Validated,
}

/// <param name="DiscountAffectingBase">Line discount, charge code 00 (reduces the taxable base).</param>
/// <param name="ChargeAffectingBase">Line charge, code 47.</param>
/// <param name="DiscountNotAffectingBase">Line discount, code 01.</param>
/// <param name="ChargeNotAffectingBase">Line charge, code 48.</param>
public sealed record DocumentLineDto(
    int LineNumber,
    string Description,
    string UnitCode,
    string? ProductCode,
    decimal Quantity,
    decimal LineExtensionAmount,
    string TaxCode,
    decimal TotalTaxAmount,
    decimal? UnitPriceIncludingTaxes,
    decimal UnitValue = 0m,
    string IgvAffectationCode = "",
    decimal DiscountAffectingBase = 0m,
    decimal ChargeAffectingBase = 0m,
    decimal DiscountNotAffectingBase = 0m,
    decimal ChargeNotAffectingBase = 0m);

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
    DateTimeOffset CreatedAt,
    NoteInfo? Note = null,
    GlobalAdjustments? Adjustments = null,
    IReadOnlyList<Installment>? Installments = null,
    string OperationTypeCode = OperationTypes.Sale,
    decimal? InitialPayment = null,
    Detraction? Detraction = null,
    IgvRetention? Retention = null)
{
    public string FullNumber => $"{Series}-{Number}";
}

/// <summary>
/// Tells Billing whether a document has been annulled. The electronic-invoicing module implements it (the annulment lives in its records);
/// without it Billing treats no document as voided.
/// </summary>
public interface IVoidStatusProvider
{
    /// <summary>True when the document is voided, or when a request to void it (a voided-documents communication or a summary line of status 3) is still pending.</summary>
    Task<bool> IsVoidedOrBeingVoidedAsync(Guid documentId, CancellationToken cancellationToken);
}

/// <summary>
/// Tells Billing which documents no longer count: the ones SUNAT rejected and the ones that were voided. The electronic-invoicing module implements it; without it
/// Billing counts every document.
/// </summary>
public interface IIneffectiveDocumentsProvider
{
    /// <summary>The subset of <paramref name="documentIds"/> that was rejected by SUNAT or is voided.</summary>
    Task<IReadOnlySet<Guid>> FindAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken);
}

public interface IDocumentService
{
    /// <summary>
    /// Creates and numbers a document. Idempotent per <paramref name="idempotencyKey"/>: the same key with the same content returns the
    /// original document; the same key with different content is a conflict.
    /// </summary>
    Task<Result<DocumentDto>> CreateAsync(string idempotencyKey, CreateDocumentRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Creates and numbers a credit or debit note with the same guarantees as <see cref="CreateAsync"/> (idempotent, gap-free, rates from rules,
    /// insert-only). A credit note cannot exceed the document it modifies.
    /// </summary>
    Task<Result<DocumentDto>> CreateNoteAsync(string idempotencyKey, CreateNoteRequest request, CancellationToken cancellationToken);

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
