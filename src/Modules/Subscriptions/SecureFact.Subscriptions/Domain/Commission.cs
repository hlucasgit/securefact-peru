namespace SecureFact.Subscriptions.Domain;

/// <summary>One version of the commission terms for resellers. Versions are only added; an account keeps the one that applied the day it came under its reseller (ADR-063).</summary>
internal sealed class CommissionSchedule
{
    private CommissionSchedule()
    {
    }

    public Guid Id { get; private set; }

    public int Version { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    public string? Note { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static CommissionSchedule Create(int version, DateOnly effectiveFrom, string? note, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        Version = version,
        EffectiveFrom = effectiveFrom,
        Note = note,
        CreatedAt = now,
    };
}

/// <summary>From <see cref="MinAccounts"/> active accounts of the reseller on, the commission is <see cref="Rate"/> of what the accounts pay without tax.</summary>
internal sealed class CommissionTier
{
    private CommissionTier()
    {
    }

    public Guid Id { get; private set; }

    public Guid ScheduleId { get; private set; }

    public int MinAccounts { get; private set; }

    public decimal Rate { get; private set; }

    public static CommissionTier Create(Guid scheduleId, int minAccounts, decimal rate) => new()
    {
        Id = Guid.CreateVersion7(),
        ScheduleId = scheduleId,
        MinAccounts = minAccounts,
        Rate = rate,
    };
}

/// <summary>What a reseller earned for one payment (or lost for one reversed). Only added: a reversed payment adds an entry of the opposite amount.</summary>
internal sealed class CommissionEntry
{
    private CommissionEntry()
    {
    }

    public Guid Id { get; private set; }

    public Guid ResellerId { get; private set; }

    public Guid TenantId { get; private set; }

    public string TenantName { get; private set; } = string.Empty;

    public Guid ChargeId { get; private set; }

    public DateOnly ChargePeriod { get; private set; }

    public Guid PaymentId { get; private set; }

    /// <summary>First day of the month (Lima) in which the payment was recorded. The statement and the settlement of a month are made of its entries.</summary>
    public DateOnly Month { get; private set; }

    /// <summary>The part of the payment that is not tax.</summary>
    public decimal BaseAmount { get; private set; }

    public decimal Rate { get; private set; }

    public decimal Amount { get; private set; }

    public Guid ScheduleId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static CommissionEntry Create(
        Guid resellerId, Guid tenantId, string tenantName, Guid chargeId, DateOnly chargePeriod, Guid paymentId, DateOnly month, decimal baseAmount, decimal rate, decimal amount, Guid scheduleId,
        DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        ResellerId = resellerId,
        TenantId = tenantId,
        TenantName = tenantName,
        ChargeId = chargeId,
        ChargePeriod = chargePeriod,
        PaymentId = paymentId,
        Month = month,
        BaseAmount = baseAmount,
        Rate = rate,
        Amount = amount,
        ScheduleId = scheduleId,
        CreatedAt = now,
    };
}

/// <summary>The platform settled the commission of a month with a reseller. One per reseller and month, and only after the month closed, so its total never changes.</summary>
internal sealed class CommissionSettlement
{
    private CommissionSettlement()
    {
    }

    public Guid Id { get; private set; }

    public Guid ResellerId { get; private set; }

    public DateOnly Month { get; private set; }

    public decimal Total { get; private set; }

    public int Entries { get; private set; }

    public DateOnly SettledOn { get; private set; }

    public string? Reference { get; private set; }

    public string? Note { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static CommissionSettlement Create(Guid resellerId, DateOnly month, decimal total, int entries, DateOnly settledOn, string? reference, string? note, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        ResellerId = resellerId,
        Month = month,
        Total = total,
        Entries = entries,
        SettledOn = settledOn,
        Reference = reference,
        Note = note,
        CreatedAt = now,
    };
}
