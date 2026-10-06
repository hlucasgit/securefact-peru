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

    public void ChangePlan(Guid planId) => PlanId = planId;

    public void AssignReseller(Guid? resellerId) => ResellerId = resellerId;

    public static Tenant Create(Guid id, string name, TenantEnvironment environment, Guid? resellerId, Guid planId, DateTimeOffset now) => new()
    {
        Id = id,
        Name = name,
        Status = TenantStatus.Active,
        Environment = environment,
        ResellerId = resellerId,
        PlanId = planId,
        CreatedAt = now,
    };
}
