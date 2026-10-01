namespace SecureFact.Audit.Contracts;

/// <summary>Stable action names, <c>module.entity.verb</c>. Never rename a published action: history depends on them.</summary>
public static class AuditActions
{
    public const string TenantCreated = "tenancy.tenant.created";

    public const string CompanyCreated = "organizations.company.created";
    public const string CompanyUpdated = "organizations.company.updated";
    public const string CompanyDeactivated = "organizations.company.deactivated";
    public const string EstablishmentCreated = "organizations.establishment.created";
    public const string EstablishmentUpdated = "organizations.establishment.updated";
    public const string EstablishmentDeactivated = "organizations.establishment.deactivated";

    public const string LoginSucceeded = "identity.login.succeeded";
    public const string LoginFailed = "identity.login.failed";
    public const string AccountLocked = "identity.account.locked";
    public const string RefreshReuseDetected = "identity.session.refresh_reuse_detected";
    public const string Logout = "identity.session.logout";
    public const string SessionsRevoked = "identity.sessions.revoked";
    public const string MfaEnabled = "identity.mfa.enabled";
    public const string PasswordResetRequested = "identity.password_reset.requested";
    public const string PasswordResetCompleted = "identity.password_reset.completed";
    public const string UserCreated = "identity.user.created";
    public const string UserRoleAssigned = "identity.user.role_assigned";
    public const string UserRoleRemoved = "identity.user.role_removed";
    public const string UserDeactivated = "identity.user.deactivated";
    public const string PlatformAdminBootstrapped = "identity.platform_admin.bootstrapped";
}

/// <summary>
/// One auditable fact. Values must never contain secrets (passwords, tokens, keys): the module also redacts
/// well-known sensitive property names as a safety net, but callers are responsible for what they pass.
/// </summary>
public sealed record AuditEvent(
    string Action,
    string EntityType,
    string? EntityId,
    Guid? TenantId,
    IReadOnlyDictionary<string, object?>? OldValues = null,
    IReadOnlyDictionary<string, object?>? NewValues = null,
    Guid? ActorUserId = null);

public sealed record AuditRecord(
    Guid Id,
    Guid? TenantId,
    long Sequence,
    DateTimeOffset OccurredAt,
    string ActorType,
    Guid? ActorUserId,
    string Action,
    string EntityType,
    string? EntityId,
    string? OldValues,
    string? NewValues,
    string? IpAddress,
    string? UserAgent,
    string? CorrelationId,
    string? RequestId);

public sealed record AuditVerification(bool IsIntact, long EventsChecked, long? FirstBrokenSequence, string? Reason);

/// <summary>Appends to the tamper-evident, append-only audit trail.</summary>
public interface IAuditTrail
{
    Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}

public interface IAuditQuery
{
    /// <summary>Events visible to the current scope, newest first. <paramref name="tenantId"/> is honoured only in platform scope.</summary>
    Task<IReadOnlyList<AuditRecord>> ListAsync(Guid? tenantId, string? action, string? entityType, string? entityId, int skip, int take, CancellationToken cancellationToken);

    /// <summary>Recomputes the hash chain of one tenant (or of the platform when null) and reports the first inconsistency.</summary>
    Task<AuditVerification> VerifyAsync(Guid? tenantId, CancellationToken cancellationToken);
}
