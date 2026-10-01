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
}
