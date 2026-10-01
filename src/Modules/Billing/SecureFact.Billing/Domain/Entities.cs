using SecureFact.Billing.Contracts;
using SecureFact.Platform.Persistence;

namespace SecureFact.Billing.Domain;

internal sealed class Series : ITenantOwned
{
    public const long MaxNumber = 99_999_999;

    private Series()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid CompanyId { get; private set; }

    public Guid? EstablishmentId { get; private set; }

    public string DocumentTypeCode { get; private set; } = string.Empty;

    public string Code { get; private set; } = string.Empty;

    /// <summary>Last issued number. Only ever advanced by the atomic <c>UPDATE … RETURNING</c> in the numbering service.</summary>
    public long LastNumber { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Series Create(Guid id, Guid tenantId, Guid companyId, Guid? establishmentId, string documentTypeCode, string code, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        CompanyId = companyId,
        EstablishmentId = establishmentId,
        DocumentTypeCode = documentTypeCode,
        Code = code,
        IsActive = true,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        UpdatedAt = now;
    }
}

/// <summary>A numbered, fully calculated document. Rows are insert-only: the database grants no UPDATE on this table.</summary>
internal sealed class Document : ITenantOwned
{
    private readonly List<DocumentLine> _lines = [];

    private Document()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid CompanyId { get; private set; }

    public Guid SeriesId { get; private set; }

    public string DocumentTypeCode { get; private set; } = string.Empty;

    public string SeriesCode { get; private set; } = string.Empty;

    public long Number { get; private set; }

    public DateOnly IssueDate { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public string BuyerDocumentTypeCode { get; private set; } = string.Empty;

    public string BuyerDocumentNumber { get; private set; } = string.Empty;

    public string BuyerName { get; private set; } = string.Empty;

    public string? BuyerAddress { get; private set; }

    public string? BuyerEmail { get; private set; }

    public DocumentStatus Status { get; private set; }

    public decimal PayableAmount { get; private set; }

    /// <summary>Complete <c>TaxCalculationResult</c> as JSON, so the numbers that were issued can always be reproduced.</summary>
    public string TotalsJson { get; private set; } = string.Empty;

    /// <summary>The request exactly as received (after normalisation of nothing): evidence of what the client asked for.</summary>
    public string OriginalRequestJson { get; private set; } = string.Empty;

    public byte[] RequestHash { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Notes only: the document the note modifies, and why (catalogue 09 or 10 code plus the explanation).</summary>
    public Guid? ReferencedDocumentId { get; private set; }

    public string? ReferencedDocumentTypeCode { get; private set; }

    public string? ReferencedSeries { get; private set; }

    public long? ReferencedNumber { get; private set; }

    public string? ReasonCode { get; private set; }

    public string? Reason { get; private set; }

    public IReadOnlyCollection<DocumentLine> Lines => _lines;

    public static Document Create(
        Guid id, Guid tenantId, Guid companyId, Guid seriesId, string documentTypeCode, string seriesCode, long number, DateOnly issueDate,
        string currency, BuyerSnapshot buyer, decimal payable, string totalsJson, string originalRequestJson, byte[] requestHash, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        CompanyId = companyId,
        SeriesId = seriesId,
        DocumentTypeCode = documentTypeCode,
        SeriesCode = seriesCode,
        Number = number,
        IssueDate = issueDate,
        Currency = currency,
        BuyerDocumentTypeCode = buyer.DocumentTypeCode,
        BuyerDocumentNumber = buyer.DocumentNumber,
        BuyerName = buyer.Name,
        BuyerAddress = buyer.Address,
        BuyerEmail = buyer.Email,
        Status = DocumentStatus.Validated,
        PayableAmount = payable,
        TotalsJson = totalsJson,
        OriginalRequestJson = originalRequestJson,
        RequestHash = requestHash,
        CreatedAt = now,
    };

    public void AddLine(DocumentLine line) => _lines.Add(line);

    public void MarkAsNote(Guid referencedId, string referencedType, string referencedSeries, long referencedNumber, string reasonCode, string reason)
    {
        ReferencedDocumentId = referencedId;
        ReferencedDocumentTypeCode = referencedType;
        ReferencedSeries = referencedSeries;
        ReferencedNumber = referencedNumber;
        ReasonCode = reasonCode;
        Reason = reason;
    }
}

internal sealed class DocumentLine : ITenantOwned
{
    private DocumentLine()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid DocumentId { get; private set; }

    public int LineNumber { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public string UnitCode { get; private set; } = string.Empty;

    public string? ProductCode { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal UnitValue { get; private set; }

    public string AffectationCode { get; private set; } = string.Empty;

    public decimal LineExtensionAmount { get; private set; }

    public string TaxCode { get; private set; } = string.Empty;

    public decimal TotalTaxAmount { get; private set; }

    public decimal? UnitPriceIncludingTaxes { get; private set; }

    public static DocumentLine Create(
        Guid tenantId, Guid documentId, int lineNumber, string description, string unitCode, string? productCode, decimal quantity,
        decimal unitValue, string affectationCode, decimal lineExtension, string taxCode, decimal totalTax, decimal? unitPriceIncl) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        DocumentId = documentId,
        LineNumber = lineNumber,
        Description = description,
        UnitCode = unitCode,
        ProductCode = productCode,
        Quantity = quantity,
        UnitValue = unitValue,
        AffectationCode = affectationCode,
        LineExtensionAmount = lineExtension,
        TaxCode = taxCode,
        TotalTaxAmount = totalTax,
        UnitPriceIncludingTaxes = unitPriceIncl,
    };
}

/// <summary>Remembers the outcome of an idempotency key. Insert-only.</summary>
internal sealed class IdempotencyRecord : ITenantOwned
{
    private IdempotencyRecord()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public byte[] RequestHash { get; private set; } = [];

    public Guid DocumentId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
