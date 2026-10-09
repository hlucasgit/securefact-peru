using SecureFact.Platform.Persistence;

namespace SecureFact.Subscriptions.Domain;

/// <summary>
/// What a tenant owes for one month. Every figure is copied from the terms of the day it was issued, so nothing that changes later (a price, a plan, a name) rewrites it. Only the void mark can
/// change, once.
/// </summary>
internal sealed class Charge : ITenantOwned
{
    private Charge()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string TenantName { get; private set; } = string.Empty;

    /// <summary>First day of the month that the charge is for.</summary>
    public DateOnly Period { get; private set; }

    public Guid PlanId { get; private set; }

    public string PlanCode { get; private set; } = string.Empty;

    public string PlanName { get; private set; } = string.Empty;

    public Guid PriceId { get; private set; }

    public decimal MonthlyFee { get; private set; }

    public int? IncludedDocuments { get; private set; }

    public int DocumentsIssued { get; private set; }

    public int OverageDocuments { get; private set; }

    public decimal? OverageUnitPrice { get; private set; }

    public decimal OverageAmount { get; private set; }

    public decimal NetAmount { get; private set; }

    public decimal TaxRate { get; private set; }

    public decimal TaxAmount { get; private set; }

    public decimal TotalAmount { get; private set; }

    public string Currency { get; private set; } = "PEN";

    public DateOnly IssuedOn { get; private set; }

    public DateOnly DueOn { get; private set; }

    /// <summary>The day from which an unpaid balance suspends the account; null when the policy of the day never suspends.</summary>
    public DateOnly? SuspendOn { get; private set; }

    /// <summary>The reseller that the tenant had when the charge was issued; the commission of its payments goes to it. Null for a tenant without reseller.</summary>
    public Guid? ResellerId { get; private set; }

    /// <summary>The commission schedule that applies to the tenant, fixed when it came under the reseller (ADR-063). Null when there is no reseller or no schedule.</summary>
    public Guid? CommissionScheduleId { get; private set; }

    public DateTimeOffset? VoidedAt { get; private set; }

    public string? VoidReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public uint Version { get; private set; }

    public bool IsVoid => VoidedAt is not null;

    public static Charge Create(
        Guid tenantId, string tenantName, DateOnly period, Guid planId, string planCode, string planName, Guid priceId, decimal monthlyFee, int? includedDocuments, int documentsIssued, int overageDocuments,
        decimal? overageUnitPrice, decimal overageAmount, decimal netAmount, decimal taxRate, decimal taxAmount, decimal totalAmount, DateOnly issuedOn, DateOnly dueOn, DateOnly? suspendOn, Guid? resellerId,
        Guid? commissionScheduleId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        TenantName = tenantName,
        Period = period,
        PlanId = planId,
        PlanCode = planCode,
        PlanName = planName,
        PriceId = priceId,
        MonthlyFee = monthlyFee,
        IncludedDocuments = includedDocuments,
        DocumentsIssued = documentsIssued,
        OverageDocuments = overageDocuments,
        OverageUnitPrice = overageUnitPrice,
        OverageAmount = overageAmount,
        NetAmount = netAmount,
        TaxRate = taxRate,
        TaxAmount = taxAmount,
        TotalAmount = totalAmount,
        Currency = "PEN",
        IssuedOn = issuedOn,
        DueOn = dueOn,
        SuspendOn = suspendOn,
        ResellerId = resellerId,
        CommissionScheduleId = commissionScheduleId,
        CreatedAt = now,
    };

    public void MarkVoid(string reason, DateTimeOffset now)
    {
        VoidedAt = now;
        VoidReason = reason;
    }
}
