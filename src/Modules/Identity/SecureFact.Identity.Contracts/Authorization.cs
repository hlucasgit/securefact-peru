namespace SecureFact.Identity.Contracts;

/// <summary>Explicit permission codes. A role is only a named bundle of these; nothing is implied by a role name (RBAC).</summary>
public static class Permissions
{
    public const string TenantsCreate = "tenants.create";
    public const string TenantsRead = "tenants.read";
    public const string UsersRead = "users.read";
    public const string UsersManage = "users.manage";
    public const string SessionsRevoke = "sessions.revoke";
    public const string AuditRead = "audit.read";
    public const string CompaniesRead = "companies.read";
    public const string CompaniesManage = "companies.manage";

    public static IReadOnlyList<string> All { get; } =
    [
        TenantsCreate, TenantsRead, UsersRead, UsersManage, SessionsRevoke, AuditRead, CompaniesRead, CompaniesManage,
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
        [Roles.PlatformSupport] = (RoleLevel.Platform, [Permissions.TenantsRead, Permissions.UsersRead, Permissions.AuditRead]),
        [Roles.ResellerAdmin] = (RoleLevel.Reseller, [Permissions.TenantsRead]),
        [Roles.TenantOwner] = (RoleLevel.Tenant,
            [Permissions.TenantsRead, Permissions.UsersRead, Permissions.UsersManage, Permissions.SessionsRevoke, Permissions.AuditRead, Permissions.CompaniesRead, Permissions.CompaniesManage]),
        [Roles.TenantAdmin] = (RoleLevel.Tenant,
            [Permissions.TenantsRead, Permissions.UsersRead, Permissions.UsersManage, Permissions.SessionsRevoke, Permissions.CompaniesRead, Permissions.CompaniesManage]),
        [Roles.BillingAdmin] = (RoleLevel.Tenant, [Permissions.TenantsRead, Permissions.CompaniesRead]),
        [Roles.Accountant] = (RoleLevel.Tenant, [Permissions.CompaniesRead]),
        [Roles.Sales] = (RoleLevel.Tenant, [Permissions.CompaniesRead]),
        [Roles.Developer] = (RoleLevel.Tenant, [Permissions.CompaniesRead]),
        [Roles.Auditor] = (RoleLevel.Tenant, [Permissions.AuditRead, Permissions.UsersRead, Permissions.CompaniesRead]),
        [Roles.ReadOnly] = (RoleLevel.Tenant, [Permissions.CompaniesRead]),
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
