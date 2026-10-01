using SecureFact.SharedKernel.Domain;

namespace SecureFact.SharedKernel.Tenancy;

/// <summary>
/// Tenant of the current operation. Resolved from the authenticated principal, API key or host,
/// never from a client-supplied parameter (ADR-003).
/// </summary>
public interface ITenantContext
{
    /// <summary>Null when no tenant has been resolved (anonymous or platform-level operation).</summary>
    TenantId? Current { get; }
}
