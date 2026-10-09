namespace SecureFact.Identity.Contracts;

/// <summary>Explicit permission codes. A role is only a named bundle of these; nothing is implied by a role name (RBAC).</summary>
public static class Permissions
{
    public const string TenantsCreate = "tenants.create";
    public const string TenantsRead = "tenants.read";
    public const string TenantsManage = "tenants.manage";
    public const string UsersRead = "users.read";
    public const string UsersManage = "users.manage";
    public const string SessionsRevoke = "sessions.revoke";
    public const string AuditRead = "audit.read";
    public const string CompaniesRead = "companies.read";
    public const string CompaniesManage = "companies.manage";
    public const string SeriesManage = "series.manage";
    public const string DocumentsRead = "documents.read";
    public const string DocumentsCreate = "documents.create";
    public const string CustomersRead = "customers.read";
    public const string CustomersManage = "customers.manage";
    public const string ProductsRead = "products.read";
    public const string ProductsManage = "products.manage";
    public const string CertificatesRead = "certificates.read";
    public const string CertificatesManage = "certificates.manage";
    public const string CpeSend = "cpe.send";
    public const string ResellerTenantsRead = "reseller.tenants.read";
    public const string ResellerTenantsCreate = "reseller.tenants.create";
    public const string ResellerTenantsManage = "reseller.tenants.manage";
    public const string ResellerBrandingManage = "reseller.branding.manage";
    public const string ResellerTenantsSuspend = "reseller.tenants.suspend";
    public const string SubscriptionsRead = "subscriptions.read";
    public const string SubscriptionsManage = "subscriptions.manage";
    public const string ResellerCommissionsRead = "reseller.commissions.read";
    public const string AccountBillingManage = "account.billing.manage";

    public static IReadOnlyList<string> All { get; } =
    [
        TenantsCreate, TenantsRead, TenantsManage, UsersRead, UsersManage, SessionsRevoke, AuditRead, CompaniesRead, CompaniesManage,
        SeriesManage, DocumentsRead, DocumentsCreate, CustomersRead, CustomersManage, ProductsRead, ProductsManage,
        CertificatesRead, CertificatesManage, CpeSend, ResellerTenantsRead, ResellerTenantsCreate, ResellerTenantsManage, ResellerBrandingManage, ResellerTenantsSuspend,
        SubscriptionsRead, SubscriptionsManage, ResellerCommissionsRead, AccountBillingManage,
    ];
}

public static class Roles
{
    public const string PlatformSuperAdmin = nameof(PlatformSuperAdmin);
    public const string PlatformSupport = nameof(PlatformSupport);
    public const string ResellerAdmin = nameof(ResellerAdmin);
    public const string TenantOwner = nameof(TenantOwner);
    public const string TenantAdmin = nameof(TenantAdmin);
    public const string BillingAdmin = nameof(BillingAdmin);
    public const string Accountant = nameof(Accountant);
    public const string Sales = nameof(Sales);
    public const string Developer = nameof(Developer);
    public const string Auditor = nameof(Auditor);
    public const string ReadOnly = nameof(ReadOnly);
}

public enum RoleLevel
{
    Platform,
    Reseller,
    Tenant,
}

/// <summary>Code-defined, versioned role-to-permission mapping. New permissions are added by the module that introduces the feature.</summary>
public static class RoleCatalog
{
    private static readonly Dictionary<string, (RoleLevel Level, string[] Permissions)> Definitions = new(StringComparer.Ordinal)
    {
        [Roles.PlatformSuperAdmin] = (RoleLevel.Platform, [.. Permissions.All]),
        // Support never reads tenant business data directly: that requires an explicit, audited delegation (future).
        [Roles.PlatformSupport] = (RoleLevel.Platform, [Permissions.TenantsRead, Permissions.UsersRead, Permissions.AuditRead, Permissions.SubscriptionsRead]),
        [Roles.ResellerAdmin] = (RoleLevel.Reseller, [Permissions.ResellerTenantsRead, Permissions.ResellerTenantsCreate, Permissions.ResellerTenantsManage, Permissions.ResellerBrandingManage, Permissions.ResellerTenantsSuspend, Permissions.ResellerCommissionsRead]),
        [Roles.TenantOwner] = (RoleLevel.Tenant,
            [Permissions.TenantsRead, Permissions.UsersRead, Permissions.UsersManage, Permissions.SessionsRevoke, Permissions.AuditRead, Permissions.CompaniesRead, Permissions.CompaniesManage,
             Permissions.SeriesManage, Permissions.DocumentsRead, Permissions.DocumentsCreate,
             Permissions.CustomersRead, Permissions.CustomersManage, Permissions.ProductsRead, Permissions.ProductsManage,
             Permissions.CertificatesRead, Permissions.CertificatesManage, Permissions.CpeSend, Permissions.AccountBillingManage]),
        [Roles.TenantAdmin] = (RoleLevel.Tenant,
            [Permissions.TenantsRead, Permissions.UsersRead, Permissions.UsersManage, Permissions.SessionsRevoke, Permissions.CompaniesRead, Permissions.CompaniesManage,
             Permissions.SeriesManage, Permissions.DocumentsRead, Permissions.DocumentsCreate,
             Permissions.CustomersRead, Permissions.CustomersManage, Permissions.ProductsRead, Permissions.ProductsManage,
             Permissions.CertificatesRead, Permissions.CertificatesManage, Permissions.CpeSend, Permissions.AccountBillingManage]),
        [Roles.BillingAdmin] = (RoleLevel.Tenant, [Permissions.AccountBillingManage, Permissions.TenantsRead, Permissions.CompaniesRead, Permissions.SeriesManage, Permissions.DocumentsRead, Permissions.DocumentsCreate, Permissions.CpeSend, Permissions.CustomersRead, Permissions.CustomersManage, Permissions.ProductsRead, Permissions.ProductsManage]),
        [Roles.Accountant] = (RoleLevel.Tenant, [Permissions.CompaniesRead, Permissions.DocumentsRead, Permissions.CustomersRead, Permissions.ProductsRead]),
        [Roles.Sales] = (RoleLevel.Tenant, [Permissions.CompaniesRead, Permissions.DocumentsRead, Permissions.DocumentsCreate, Permissions.CustomersRead, Permissions.CustomersManage, Permissions.ProductsRead]),
        [Roles.Developer] = (RoleLevel.Tenant, [Permissions.CompaniesRead]),
        [Roles.Auditor] = (RoleLevel.Tenant, [Permissions.AuditRead, Permissions.UsersRead, Permissions.CompaniesRead, Permissions.DocumentsRead, Permissions.CustomersRead, Permissions.ProductsRead, Permissions.CertificatesRead]),
        [Roles.ReadOnly] = (RoleLevel.Tenant, [Permissions.CompaniesRead, Permissions.DocumentsRead, Permissions.CustomersRead, Permissions.ProductsRead]),
    };

    public static IReadOnlyCollection<string> AllRoles => Definitions.Keys;

    public static bool Exists(string role) => Definitions.ContainsKey(role);

    public static RoleLevel LevelOf(string role) => Definitions[role].Level;

    public static IReadOnlySet<string> PermissionsOf(string role) =>
        Definitions.TryGetValue(role, out var definition) ? definition.Permissions.ToHashSet(StringComparer.Ordinal) : new HashSet<string>();

    public static IReadOnlySet<string> PermissionsOf(IEnumerable<string> roles)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            set.UnionWith(PermissionsOf(role));
        }

        return set;
    }

    /// <summary>
    /// Privilege-escalation guard: an actor may grant a role only when (a) the role level suits the target user,
    /// (b) the actor is platform staff for platform/reseller roles, and (c) the actor already holds every permission of that role.
    /// </summary>
    public static bool CanAssign(IEnumerable<string> actorRoles, bool actorIsPlatform, string targetRole)
    {
        if (!Exists(targetRole))
        {
            return false;
        }

        if (LevelOf(targetRole) != RoleLevel.Tenant && !actorIsPlatform)
        {
            return false;
        }

        return PermissionsOf(actorRoles).IsSupersetOf(PermissionsOf(targetRole));
    }
}
