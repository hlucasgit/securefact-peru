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

/// <summary>Who suspended a tenant. A suspension of the platform can be lifted only by the platform; one of the reseller, by either.</summary>
public enum SuspensionSource
{
    Platform,
    Reseller,
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
    Guid PlanId,
    SuspensionSource? SuspendedBy = null);

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

    /// <summary>
    /// Suspends a tenant of the reseller, or reactivates one that the <b>reseller</b> suspended, with the reason (audited). A reseller never closes a tenant and never lifts a suspension of
    /// the platform (<c>SF-TEN-004</c>): suspending for a debt is the reseller's to do, suspending for abuse is not its to undo.
    /// </summary>
    Task<Result<TenantDto>> ChangeTenantStatusAsync(Guid resellerId, TenantId tenantId, TenantStatus target, string reason, CancellationToken cancellationToken);
}

/// <summary>How the interface presents itself to the users of a reseller (white label). Only what any visitor may see: nothing here is secret.</summary>
public sealed record BrandingDto(Guid ResellerId, string BrandName, string PrimaryColor, string? SupportEmail, string? LogoVersion);

/// <summary>What a reseller or the platform edits. <paramref name="Host"/> is the host name at which the portal of the reseller is served; only the platform sets it.</summary>
public sealed record BrandingSettings(Guid ResellerId, string ResellerName, string? BrandName, string? PrimaryColor, string? SupportEmail, string? Host, string? LogoVersion, DomainStatus HostStatus = DomainStatus.None);

public sealed record BrandingInput(string? BrandName, string? PrimaryColor, string? SupportEmail);

public sealed record BrandLogo(byte[] Data, string ContentType, string Version);

/// <summary>
/// White label (ADR-044). The first group is read by anyone, including a visitor who has not signed in; it answers only for an active reseller that has set its brand, and answers null
/// otherwise so the interface keeps the default look. The second group is for platform staff and for the reseller itself, and every method checks it is one of them.
/// </summary>
public interface IBranding
{
    /// <summary>The brand of the reseller whose portal is served at <paramref name="host"/>, or null. Only a verified domain answers (ADR-051).</summary>
    Task<BrandingDto?> ForHostAsync(string host, CancellationToken cancellationToken);

    /// <summary>The brand of the reseller of a tenant, or null for a tenant without reseller (or whose reseller has no brand or is off).</summary>
    Task<BrandingDto?> ForTenantAsync(TenantId tenantId, CancellationToken cancellationToken);

    Task<BrandingDto?> ForResellerAsync(Guid resellerId, CancellationToken cancellationToken);

    /// <summary>The verified domain at which the portal of a reseller is served, or null when it has none, it is not verified or the reseller is off. For the links in the e-mails (ADR-052).</summary>
    Task<string?> PortalHostAsync(Guid resellerId, CancellationToken cancellationToken);

    Task<BrandLogo?> LogoAsync(Guid resellerId, CancellationToken cancellationToken);

    Task<Result<BrandingSettings>> GetAsync(Guid resellerId, CancellationToken cancellationToken);

    /// <summary>Sets name, colour and support e-mail. A null name removes the brand (the default look returns). The colour must keep the white text readable on it (WCAG contrast 4.5:1).</summary>
    Task<Result<BrandingSettings>> UpdateAsync(Guid resellerId, BrandingInput input, CancellationToken cancellationToken);

    /// <summary>A PNG, JPEG or WebP of at most 200 KB, checked by its content and not by what the caller says it is.</summary>
    Task<Result<BrandingSettings>> SetLogoAsync(Guid resellerId, byte[] data, CancellationToken cancellationToken);

    Task<Result<BrandingSettings>> RemoveLogoAsync(Guid resellerId, CancellationToken cancellationToken);
}

public enum DomainStatus
{
    /// <summary>The reseller has no domain.</summary>
    None,

    /// <summary>The platform assigned a domain and the reseller has not yet proved it controls it, nor pointed it at the platform.</summary>
    Pending,

    /// <summary>The ownership proof (a TXT record) and the route (the name leads to the platform's edge) were both found. Only a verified domain shows the brand and gets a certificate.</summary>
    Verified,

    /// <summary>It was verified and the checks fail now (several times in a row): the brand and the certificates stop, and the domain comes back by itself when the DNS is right again.</summary>
    Unreachable,
}

/// <summary>
/// The domain of a reseller's portal and what is left to do for it to work. The reseller creates two DNS records, which this describes; nothing else is its work: the certificate is
/// issued by the platform's edge once the domain is verified (ADR-051).
/// </summary>
public sealed record DomainDto(
    Guid ResellerId,
    string? Host,
    DomainStatus Status,
    string? TxtName,
    string? TxtValue,
    string? CnameTarget,
    IReadOnlyList<string> EdgeAddresses,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? CheckedAt,
    string? Error);

/// <summary>What the DNS says about a name: the aliases it goes through and the addresses it ends in.</summary>
public sealed record DomainRoute(IReadOnlyList<string> Cnames, IReadOnlyList<string> Addresses);

/// <summary>The DNS lookups that verify a domain. A lookup that cannot be done (the resolver does not answer) throws; a name that does not exist or has no such record answers empty.</summary>
public interface IDomainNameSystem
{
    Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken cancellationToken);

    Task<DomainRoute> RouteAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Domains of the resellers (ADR-051): assigning one, proving it, and telling the platform's edge which names may have a certificate.</summary>
public interface IDomains
{
    /// <summary>The state of the domain of a reseller. Platform staff for any reseller; a reseller user for its own.</summary>
    Task<Result<DomainDto>> GetAsync(Guid resellerId, CancellationToken cancellationToken);

    /// <summary>Assigns the domain (platform staff only), or clears it with null. A new domain starts pending with a new proof to publish; the same one changes nothing.</summary>
    Task<Result<DomainDto>> SetAsync(Guid resellerId, string? host, CancellationToken cancellationToken);

    /// <summary>Checks the DNS now. A check is not repeated within seconds of the last one (<c>SF-DOM-002</c>).</summary>
    Task<Result<DomainDto>> VerifyAsync(Guid resellerId, CancellationToken cancellationToken);

    /// <summary>The background pass: checks the pending domains, and the verified ones that are due again. Platform scope; returns how many it checked.</summary>
    Task<int> VerifyDueAsync(CancellationToken cancellationToken);

    /// <summary>Whether the edge may ask for a certificate for this name: one of the platform's own hosts, or the verified domain of a reseller that is on.</summary>
    Task<bool> IsAllowedForCertificateAsync(string host, CancellationToken cancellationToken);
}
