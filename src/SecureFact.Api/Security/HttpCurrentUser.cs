using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using SecureFact.Identity;
using SecureFact.Identity.Contracts;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Tenancy;

namespace SecureFact.Api.Security;

/// <summary>Principal built only from claims of a validated access token (never from request parameters).</summary>
public sealed class HttpCurrentUser(Func<ClaimsPrincipal?> principal) : ICurrentUser
{
    private ClaimsPrincipal? Principal => principal();

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId => ParseGuid(JwtRegisteredClaimNames.Sub);

    public Guid? SessionId => ParseGuid(IdentityModule.SessionClaim);

    public TenantId? TenantId => ParseGuid(IdentityModule.TenantClaim) is { } id ? new TenantId(id) : null;

    public IReadOnlySet<string> Roles => Principal is null
        ? new HashSet<string>()
        : Principal.FindAll(IdentityModule.RoleClaim).Select(c => c.Value).Where(RoleCatalog.Exists).ToHashSet(StringComparer.Ordinal);

    public bool IsPlatform => Roles.Any(r => RoleCatalog.LevelOf(r) == RoleLevel.Platform);

    public Guid? ResellerId => Roles.Any(r => RoleCatalog.LevelOf(r) == RoleLevel.Reseller) ? ParseGuid(IdentityModule.ResellerClaim) : null;

    public IReadOnlySet<string> Permissions => RoleCatalog.PermissionsOf(Roles);

    public bool HasPermission(string permission) => Permissions.Contains(permission);

    public bool IsSupportAccess => Principal?.FindFirst(IdentityModule.SupportClaim) is not null;

    private Guid? ParseGuid(string claim) =>
        Guid.TryParse(Principal?.FindFirst(claim)?.Value, out var value) ? value : null;
}
