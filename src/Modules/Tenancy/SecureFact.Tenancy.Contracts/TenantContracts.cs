using SecureFact.SharedKernel;
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

public sealed record CreateTenantRequest(string Name, TenantEnvironment Environment, Guid? ResellerId = null, Guid? PlanId = null);

public sealed record TenantDto(
    TenantId Id,
    string Name,
    TenantStatus Status,
    TenantEnvironment Environment,
    Guid? ResellerId,
    DateTimeOffset CreatedAt,
    Guid PlanId);

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

/// <summary>What a plan allows. A null limit means unlimited. A plan with a <paramref name="ResellerId"/> is a private offer of that reseller; without one it is of the public catalogue.</summary>
public sealed record PlanDto(Guid Id, string Code, string Name, int? MaxCompanies, int? MaxUsers, int? MaxDocumentsPerMonth, bool IsActive, Guid? ResellerId = null);

public sealed record PlanInput(string Code, string Name, int? MaxCompanies, int? MaxUsers, int? MaxDocumentsPerMonth, bool IsActive = true, Guid? ResellerId = null);

/// <summary>The plan catalogue and the plan of each tenant, for platform staff. A tenant reads only its own plan, through <see cref="IPlanLimits"/>: it never sees the other plans.</summary>
public interface IPlanAdministration
{
    Task<Result<IReadOnlyList<PlanDto>>> ListAsync(CancellationToken cancellationToken);

    Task<Result<PlanDto>> CreateAsync(PlanInput input, CancellationToken cancellationToken);

    /// <summary>Changes name, limits and whether new tenants can pick the plan. The code never changes. Lowering a limit never removes what a tenant already holds.</summary>
    Task<Result<PlanDto>> UpdateAsync(Guid id, PlanInput input, CancellationToken cancellationToken);

    /// <summary>Moves a tenant to an active plan (audited, with the previous plan).</summary>
    Task<Result<TenantDto>> AssignAsync(TenantId tenantId, Guid planId, CancellationToken cancellationToken);
}

public enum PlanResource
{
    Companies,
    Users,
    DocumentsPerMonth,
}

/// <summary>The limits that apply to a tenant, for the modules that must refuse what a plan does not allow.</summary>
public interface IPlanLimits
{
    /// <summary>The plan of the tenant, or null when it does not exist or is not visible to the current scope.</summary>
    Task<PlanDto?> OfTenantAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Ok while adding one more keeps <paramref name="current"/> + 1 within the limit of the tenant's plan; otherwise <c>SF-PLAN-001</c>. The caller counts what it owns: Tenancy
    /// knows the plan, not the companies, users or documents.
    /// </summary>
    Task<Result<Unit>> EnsureCanAddAsync(TenantId tenantId, PlanResource resource, int current, CancellationToken cancellationToken);

    /// <summary>The limit of the tenant's plan for the resource (null: unlimited), for callers that count inside their own transaction.</summary>
    Task<int?> LimitAsync(TenantId tenantId, PlanResource resource, CancellationToken cancellationToken);

    /// <summary>The refusal for a resource that is at its limit.</summary>
    static Error LimitReached(PlanResource resource, string planName, int limit) => Error.Forbidden(
        ErrorCodes.PlanLimitReached,
        "Límite del plan alcanzado",
        $"El plan {planName} permite hasta {limit} {Describe(resource)}. Cambie de plan para continuar.");

    private static string Describe(PlanResource resource) => resource switch
    {
        PlanResource.Companies => "empresas",
        PlanResource.Users => "usuarios",
        _ => "comprobantes por mes",
    };
}

public sealed record ResellerDto(Guid Id, string Name, bool IsActive, int TenantCount, DateTimeOffset CreatedAt);

/// <summary>A tenant that a reseller opens for one of its customers. Without a plan it starts on the default one.</summary>
public sealed record ResellerTenantRequest(string Name, TenantEnvironment Environment, Guid? PlanId = null);

/// <summary>
/// Resellers (ADR-043). The first group is platform staff; the second is the reseller's own view, and every method there takes the reseller from the token (never from the request) and
/// answers "not found" for a tenant or plan that is not its own, so it cannot tell whether another reseller's tenant exists.
/// </summary>
public interface IResellerAdministration
{
    Task<Result<IReadOnlyList<ResellerDto>>> ListAsync(CancellationToken cancellationToken);

    Task<Result<ResellerDto>> CreateAsync(string name, CancellationToken cancellationToken);

    /// <summary>Renames a reseller or switches it off. A reseller that is off cannot sign in and cannot act; its tenants keep working.</summary>
    Task<Result<ResellerDto>> UpdateAsync(Guid id, string name, bool isActive, CancellationToken cancellationToken);

    /// <summary>Moves a tenant to a reseller, or to none (null). Audited with the previous reseller.</summary>
    Task<Result<TenantDto>> AssignTenantAsync(TenantId tenantId, Guid? resellerId, CancellationToken cancellationToken);

    /// <summary>True when the reseller exists and is on (sign-in and every request of a reseller user).</summary>
    Task<bool> IsActiveAsync(Guid resellerId, CancellationToken cancellationToken);

    Task<Result<ResellerDto>> GetOwnAsync(Guid resellerId, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<TenantDto>>> ListTenantsAsync(Guid resellerId, string? search, int skip, int take, CancellationToken cancellationToken);

    Task<Result<TenantDto>> GetTenantAsync(Guid resellerId, TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>Opens a tenant for the reseller, on a plan it may assign. The owner of the tenant is created next, by Identity.</summary>
    Task<Result<TenantDto>> CreateTenantAsync(Guid resellerId, ResellerTenantRequest request, CancellationToken cancellationToken);

    /// <summary>The active plans a reseller may assign: the public catalogue and its own private plans.</summary>
    Task<Result<IReadOnlyList<PlanDto>>> ListPlansAsync(Guid resellerId, CancellationToken cancellationToken);

    Task<Result<TenantDto>> AssignPlanAsync(Guid resellerId, TenantId tenantId, Guid planId, CancellationToken cancellationToken);
}
