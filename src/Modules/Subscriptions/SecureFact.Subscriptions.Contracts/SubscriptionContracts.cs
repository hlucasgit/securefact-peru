using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Subscriptions.Contracts;

/// <summary>How a payment reached the platform. The platform does not take cards itself (ADR-062): a person records what was paid.</summary>
public enum PaymentMethod
{
    Transfer,
    Deposit,
    Cash,
    Card,
    Other,
}

/// <summary>Derived from the payments and the due date, never stored: it cannot disagree with them.</summary>
public enum ChargeStatus
{
    Pending,
    Partial,
    Overdue,
    Paid,
    Void,
}

/// <param name="EffectiveFrom">The first day of a month. Never in the past, and after the previous version of the plan: published versions are never rewritten.</param>
/// <param name="MonthlyFee">Soles without tax. Zero is a free plan that is still tracked.</param>
/// <param name="IncludedDocuments">For a plan that charges the overage: how many documents of the month the fee covers. Empty for the other plans.</param>
/// <param name="OverageUnitPrice">For a plan that charges the overage: soles without tax for each document over the included ones. Empty for the other plans.</param>
public sealed record PlanPriceInput(DateOnly EffectiveFrom, decimal MonthlyFee, int? IncludedDocuments, decimal? OverageUnitPrice, string? Note);

public sealed record PlanPriceDto(
    Guid Id, Guid PlanId, int Version, DateOnly EffectiveFrom, decimal MonthlyFee, int? IncludedDocuments, decimal? OverageUnitPrice, string? Note, DateTimeOffset CreatedAt);

/// <param name="DueDays">Days from the issue of a charge to its due date.</param>
/// <param name="SuspendAfterDays">Days after the due date at which an unpaid charge suspends the account; empty never suspends.</param>
public sealed record BillingPolicyInput(DateOnly EffectiveFrom, int DueDays, int? SuspendAfterDays, string? Note);

public sealed record BillingPolicyDto(Guid Id, int Version, DateOnly EffectiveFrom, int DueDays, int? SuspendAfterDays, string? Note, DateTimeOffset CreatedAt);

/// <summary>
/// What a tenant pays and since when. <paramref name="Price"/> is the version that was in force the day the tenant took its plan (or the first one, when the plan had none yet): later versions do not
/// touch it. Without a price the tenant is not charged. <paramref name="FirstChargePeriod"/> is the first month it will be charged for.
/// </summary>
public sealed record TenantTermsDto(
    Guid TenantId, Guid PlanId, string PlanCode, string PlanName, bool AllowsOverage, DateTimeOffset PlanAssignedAt, PlanPriceDto? Price, DateOnly? FirstChargePeriod);

public sealed record ChargeDto(
    Guid Id,
    Guid TenantId,
    string TenantName,
    DateOnly Period,
    string PlanCode,
    string PlanName,
    decimal MonthlyFee,
    int? IncludedDocuments,
    int DocumentsIssued,
    int OverageDocuments,
    decimal? OverageUnitPrice,
    decimal OverageAmount,
    decimal NetAmount,
    decimal TaxRate,
    decimal TaxAmount,
    decimal TotalAmount,
    string Currency,
    DateOnly IssuedOn,
    DateOnly DueOn,
    DateOnly? SuspendOn,
    decimal PaidAmount,
    decimal Balance,
    ChargeStatus Status,
    string? VoidReason,
    DateTimeOffset CreatedAt);

public sealed record PaymentDto(
    Guid Id, Guid ChargeId, decimal Amount, PaymentMethod Method, string? Reference, DateOnly PaidOn, string? Note, Guid? ReversesPaymentId, DateTimeOffset RecordedAt);

public sealed record ChargeDetailDto(ChargeDto Charge, IReadOnlyList<PaymentDto> Payments);

/// <param name="TenantId">Platform staff only; a tenant always sees its own.</param>
public sealed record ChargeFilter(Guid? TenantId, ChargeStatus? Status, DateOnly? Period, int Skip, int Take);

/// <param name="Amount">Soles with tax, more than zero and at most the balance of the charge.</param>
public sealed record RecordPaymentRequest(decimal Amount, PaymentMethod Method, DateOnly PaidOn, string? Reference, string? Note);

/// <summary>Prices of the plans, the billing policy and what each tenant pays (ADR-062). The first four methods are for platform staff; the last one is read by the tenant itself too.</summary>
public interface IPricing
{
    Task<Result<IReadOnlyList<PlanPriceDto>>> ListPricesAsync(Guid planId, CancellationToken cancellationToken);

    /// <summary>Adds the next version of the price of a plan. It is checked against the plan: one that charges the overage needs the included documents and the unit price, and the others must not have them.</summary>
    Task<Result<PlanPriceDto>> PublishPriceAsync(Guid planId, PlanPriceInput input, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<BillingPolicyDto>>> ListPoliciesAsync(CancellationToken cancellationToken);

    Task<Result<BillingPolicyDto>> PublishPolicyAsync(BillingPolicyInput input, CancellationToken cancellationToken);

    Task<Result<TenantTermsDto>> TermsOfAsync(TenantId tenantId, CancellationToken cancellationToken);
}

/// <summary>Charges and their payments (ADR-062). Reading is for the tenant (its own) and for platform staff (all); writing is for platform staff.</summary>
public interface ICollections
{
    Task<Result<IReadOnlyList<ChargeDto>>> ListChargesAsync(ChargeFilter filter, CancellationToken cancellationToken);

    Task<Result<ChargeDetailDto>> GetChargeAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Records a payment. Paying off the last charge past its grace lifts a suspension for non-payment (ADR-064).</summary>
    Task<Result<PaymentDto>> RecordPaymentAsync(Guid chargeId, RecordPaymentRequest request, CancellationToken cancellationToken);

    /// <summary>Cancels a payment recorded by mistake with a payment of the opposite amount. Nothing is edited or deleted.</summary>
    Task<Result<PaymentDto>> ReversePaymentAsync(Guid paymentId, string reason, CancellationToken cancellationToken);

    /// <summary>Cancels a charge that should not exist. It must have no net payments: reverse them first.</summary>
    Task<Result<ChargeDto>> VoidChargeAsync(Guid id, string reason, CancellationToken cancellationToken);
}

public sealed record CollectionPassResult(int ChargesCreated, int TenantsSuspended, int TenantsReactivated);

/// <summary>The background pass: charges for the months that closed and the suspensions and reactivations for non-payment. Platform scope.</summary>
public interface ICollectionProcessor
{
    Task<CollectionPassResult> RunAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <param name="MinAccounts">From this many active accounts of the reseller on. The first tier starts at 0.</param>
/// <param name="Rate">Share of what the accounts pay without tax: from 0 to 1, up to four decimals.</param>
public sealed record CommissionTierInput(int MinAccounts, decimal Rate);

public sealed record CommissionScheduleInput(DateOnly EffectiveFrom, IReadOnlyList<CommissionTierInput> Tiers, string? Note);

public sealed record CommissionScheduleDto(Guid Id, int Version, DateOnly EffectiveFrom, IReadOnlyList<CommissionTierInput> Tiers, string? Note, DateTimeOffset CreatedAt);

public sealed record CommissionEntryDto(
    Guid Id, Guid TenantId, string TenantName, DateOnly ChargePeriod, Guid PaymentId, DateOnly Month, decimal BaseAmount, decimal Rate, decimal Amount, DateTimeOffset CreatedAt);

public sealed record CommissionSettlementDto(Guid Id, Guid ResellerId, DateOnly Month, decimal Total, int Entries, DateOnly SettledOn, string? Reference, string? Note, DateTimeOffset CreatedAt);

/// <summary>One month of a reseller: what it earned from the payments recorded in it and, once the platform paid it, the settlement.</summary>
public sealed record CommissionMonthDto(DateOnly Month, decimal Total, int Entries, CommissionSettlementDto? Settlement);

public sealed record CommissionStatementDto(Guid ResellerId, CommissionMonthDto Month, IReadOnlyList<CommissionEntryDto> Items);

/// <summary>What the reseller can expect: how many active accounts it has and the rate its next account would earn at that size.</summary>
public sealed record CommissionOverviewDto(Guid ResellerId, int ActiveAccounts, CommissionScheduleDto? ScheduleForNewAccounts, decimal? CurrentRate, IReadOnlyList<CommissionMonthDto> Months);

public sealed record SettleCommissionRequest(DateOnly SettledOn, string? Reference, string? Note);

/// <summary>
/// Commissions of the resellers (ADR-063). The schedules and the settlements are for platform staff; the overview and the statements are read by platform staff for any reseller and by a reseller
/// for itself, with the reseller taken from the token as everywhere else (ADR-043).
/// </summary>
public interface ICommissions
{
    Task<Result<IReadOnlyList<CommissionScheduleDto>>> ListSchedulesAsync(CancellationToken cancellationToken);

    /// <summary>Adds the next version of the terms. It applies to the accounts that come under a reseller from its date on; the accounts already there keep the version they have.</summary>
    Task<Result<CommissionScheduleDto>> PublishScheduleAsync(CommissionScheduleInput input, CancellationToken cancellationToken);

    Task<Result<CommissionOverviewDto>> OverviewAsync(Guid resellerId, CancellationToken cancellationToken);

    Task<Result<CommissionStatementDto>> StatementAsync(Guid resellerId, DateOnly month, CancellationToken cancellationToken);

    /// <summary>Records that the platform settled a closed month with the reseller. One per reseller and month; its total is the sum of the entries and never changes.</summary>
    Task<Result<CommissionSettlementDto>> SettleAsync(Guid resellerId, DateOnly month, SettleCommissionRequest request, CancellationToken cancellationToken);
}
