using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Tenancy;

namespace SecureFact.Workers.Infrastructure;

/// <summary>
/// The background worker acts as the system, not as a person: no user id (audit records the actor as "system"), no roles and no
/// permissions. It reaches tenant data only through the tenant scope the processor sets for each item.
/// </summary>
internal sealed class SystemCurrentUser : ICurrentUser
{
    public bool IsAuthenticated => false;

    public Guid? UserId => null;

    public Guid? SessionId => null;

    public TenantId? TenantId => null;

    public bool IsPlatform => false;

    public Guid? ResellerId => null;

    public IReadOnlySet<string> Roles { get; } = new HashSet<string>();

    public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();

    public bool HasPermission(string permission) => false;
}

/// <summary>Outside HTTP there is no request metadata.</summary>
internal sealed class WorkerRequestContext : IRequestContext
{
    public string? IpAddress => null;

    public string? UserAgent => "securefact-worker";

    public string? CorrelationId => null;

    public string? RequestId => null;
}
