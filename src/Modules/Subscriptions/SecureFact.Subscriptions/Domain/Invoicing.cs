using SecureFact.Platform.Persistence;
using SecureFact.Subscriptions.Contracts;

namespace SecureFact.Subscriptions.Domain;

/// <summary>With which account the platform invoices what it charges. A single row: the platform has one issuer.</summary>
internal sealed class InvoicingSetting
{
    public const int SingleId = 1;

    private InvoicingSetting()
    {
    }

    public int Id { get; private set; } = SingleId;

    public Guid IssuerTenantId { get; private set; }

    public Guid CompanyId { get; private set; }

    public Guid InvoiceSeriesId { get; private set; }

    public Guid ReceiptSeriesId { get; private set; }

    public Guid InvoiceNoteSeriesId { get; private set; }

    public Guid ReceiptNoteSeriesId { get; private set; }

    public bool Enabled { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    public static InvoicingSetting Create(InvoicingSettingsInput input, DateTimeOffset now)
    {
        var setting = new InvoicingSetting();
        setting.Apply(input, now);
        return setting;
    }

    public void Apply(InvoicingSettingsInput input, DateTimeOffset now)
    {
        IssuerTenantId = input.IssuerTenantId;
        CompanyId = input.CompanyId;
        InvoiceSeriesId = input.InvoiceSeriesId;
        ReceiptSeriesId = input.ReceiptSeriesId;
        InvoiceNoteSeriesId = input.InvoiceNoteSeriesId;
        ReceiptNoteSeriesId = input.ReceiptNoteSeriesId;
        Enabled = input.Enabled;
        UpdatedAt = now;
    }
}

/// <summary>Who a tenant is invoiced as. It is copied into each document, so changing it never changes one that was issued.</summary>
internal sealed class BillingProfile : ITenantOwned
{
    private BillingProfile()
    {
    }

    public Guid TenantId { get; private set; }

    public string DocumentTypeCode { get; private set; } = string.Empty;

    public string DocumentNumber { get; private set; } = string.Empty;

    public string LegalName { get; private set; } = string.Empty;

    public string? Address { get; private set; }

    public string? Email { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    public static BillingProfile Create(Guid tenantId, BillingProfileInput input, DateTimeOffset now)
    {
        var profile = new BillingProfile { TenantId = tenantId };
        profile.Apply(input, now);
        return profile;
    }

    public void Apply(BillingProfileInput input, DateTimeOffset now)
    {
        DocumentTypeCode = input.DocumentTypeCode.Trim();
        DocumentNumber = input.DocumentNumber.Trim();
        LegalName = input.LegalName.Trim();
        Address = string.IsNullOrWhiteSpace(input.Address) ? null : input.Address.Trim();
        Email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim();
        UpdatedAt = now;
    }
}

/// <summary>The invoice (or the credit note) that the platform issued for a charge, in the account of its issuer. Only added.</summary>
internal sealed class ChargeDocument : ITenantOwned
{
    private ChargeDocument()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The customer: the tenant that owes the charge. The document itself belongs to <see cref="IssuerTenantId"/>.</summary>
    public Guid TenantId { get; private set; }

    public Guid ChargeId { get; private set; }

    public ChargeDocumentKind Kind { get; private set; }

    public Guid IssuerTenantId { get; private set; }

    public Guid DocumentId { get; private set; }

    public string DocumentTypeCode { get; private set; } = string.Empty;

    public string Series { get; private set; } = string.Empty;

    public long Number { get; private set; }

    public DateOnly IssueDate { get; private set; }

    public decimal Total { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static ChargeDocument Create(
        Guid tenantId, Guid chargeId, ChargeDocumentKind kind, Guid issuerTenantId, Guid documentId, string documentTypeCode, string series, long number, DateOnly issueDate, decimal total, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ChargeId = chargeId,
        Kind = kind,
        IssuerTenantId = issuerTenantId,
        DocumentId = documentId,
        DocumentTypeCode = documentTypeCode,
        Series = series,
        Number = number,
        IssueDate = issueDate,
        Total = total,
        CreatedAt = now,
    };
}
