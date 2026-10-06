using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Tenancy.Contracts;

public enum TenantStatus
{
    Active,
    Suspended,
    Closed,
}

public enum TenantEnvironment
{
    Sandbox,
    Production,
}

public sealed record CreateTenantRequest(string Name, TenantEnvironment Environment, Guid? ResellerId = null);

public sealed record TenantDto(
    TenantId Id,
    string Name,
    TenantStatus Status,
    TenantEnvironment Environment,
    Guid? ResellerId,
    DateTimeOffset CreatedAt);

/// <summary>Public surface of the Tenancy module. Creating tenants is a platform-scope operation.</summary>
public interface ITenantAdministration
{
    Task<Result<TenantDto>> CreateAsync(CreateTenantRequest request, CancellationToken cancellationToken);

    /// <summary>Returns the tenant visible to the current scope: the caller's own tenant, or any tenant in platform scope.</summary>
    Task<Result<TenantDto>> GetAsync(TenantId id, CancellationToken cancellationToken);

    /// <summary>The tenants of the platform, by name and status. Platform staff only: a tenant never sees another one.</summary>
    Task<Result<IReadOnlyList<TenantDto>>> ListAsync(string? search, TenantStatus? status, int skip, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Suspends, reactivates or closes a tenant, with the reason (audited). A suspended tenant cannot sign in or use the API and can be reactivated; a closed one is final.
    /// </summary>
    Task<Result<TenantDto>> ChangeStatusAsync(TenantId id, TenantStatus target, string reason, CancellationToken cancellationToken);
}

/// <summary>
/// The status of a tenant, for the places that must refuse a suspended or closed one (sign-in, refresh, every request). Reads are cached for a few seconds in the process: a suspension
/// reaches other processes within <see cref="CacheLifetime"/>.
/// </summary>
public interface ITenantStatusReader
{
    static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);

    /// <summary>The status of the tenant, or null when it does not exist or is not visible to the current scope. <paramref name="fresh"/> skips the cache (sign-in).</summary>
    Task<TenantStatus?> GetStatusAsync(TenantId id, bool fresh, CancellationToken cancellationToken);

    /// <summary>The tenants that are suspended or closed. Read in platform scope (a tenant scope sees only its own row); never cached: the background work asks once per pass.</summary>
    Task<IReadOnlyList<Guid>> ListInactiveAsync(CancellationToken cancellationToken);
}
