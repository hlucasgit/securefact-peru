using SecureFact.Tenancy.Contracts;

namespace SecureFact.Tenancy.Domain;

internal sealed class Tenant
{
    private Tenant()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public TenantStatus Status { get; private set; }

    public TenantEnvironment Environment { get; private set; }

    public Guid? ResellerId { get; private set; }

    public Guid PlanId { get; private set; }

    /// <summary>When the tenant took its current plan. The price in force that day is the one it keeps (ADR-062).</summary>
    public DateTimeOffset PlanAssignedAt { get; private set; }

    /// <summary>When the tenant came under its current reseller; null without one. The commission schedule in force that day is the one that applies (ADR-063).</summary>
    public DateTimeOffset? ResellerAssignedAt { get; private set; }

    /// <summary>Who suspended the tenant while it is suspended; null otherwise.</summary>
    public SuspensionSource? SuspendedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Mapped to PostgreSQL <c>xmin</c> for optimistic concurrency.</summary>
    public uint Version { get; private set; }

    public void ChangeStatus(TenantStatus status, SuspensionSource? source = null)
    {
        Status = status;
        SuspendedBy = status == TenantStatus.Suspended ? source ?? SuspensionSource.Platform : null;
    }

    /// <summary>Taking the plan it already has changes nothing, so the price it keeps does not move.</summary>
    public void ChangePlan(Guid planId, DateTimeOffset now)
    {
        if (planId == PlanId)
        {
            return;
        }

        PlanId = planId;
        PlanAssignedAt = now;
    }

    public void AssignReseller(Guid? resellerId, DateTimeOffset now)
    {
        if (resellerId == ResellerId)
        {
            return;
        }

        ResellerId = resellerId;
        ResellerAssignedAt = resellerId is null ? null : now;
    }

    public static Tenant Create(Guid id, string name, TenantEnvironment environment, Guid? resellerId, Guid planId, DateTimeOffset now) => new()
    {
        Id = id,
        Name = name,
        Status = TenantStatus.Active,
        Environment = environment,
        ResellerId = resellerId,
        PlanId = planId,
        PlanAssignedAt = now,
        ResellerAssignedAt = resellerId is null ? null : now,
        CreatedAt = now,
    };
}
