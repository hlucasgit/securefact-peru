using Microsoft.EntityFrameworkCore;
using SecureFact.Identity.Contracts;
using SecureFact.Identity.Infrastructure;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Identity.Application;

/// <summary>The addresses of the active owners of an account and of the active administrators of a reseller, read in an explicit platform scope that says why (ADR-054).</summary>
internal sealed class AccountDirectory(IdentityDbContext db, DataScope scope) : IAccountDirectory
{
    public async Task<IReadOnlyList<string>> TenantOwnerEmailsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        using var elevated = scope.Elevate("notices: owners of an account");
        return await db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.IsActive && u.Roles.Any(r => r.RoleCode == Roles.TenantOwner))
            .OrderBy(u => u.EmailNormalized)
            .Select(u => u.Email)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ResellerAdminEmailsAsync(Guid resellerId, CancellationToken cancellationToken)
    {
        using var elevated = scope.Elevate("notices: administrators of a reseller");
        return await db.Users.AsNoTracking()
            .Where(u => u.ResellerId == resellerId && u.IsActive && u.Roles.Any(r => r.RoleCode == Roles.ResellerAdmin))
            .OrderBy(u => u.EmailNormalized)
            .Select(u => u.Email)
            .ToListAsync(cancellationToken);
    }
}

/// <summary>The default when nobody listens: an account is created and no notice goes out.</summary>
internal sealed class NullAccountNotices : IAccountNotices
{
    public Task AccountCreatedAsync(AccountCreatedNotice notice, CancellationToken cancellationToken) => Task.CompletedTask;
}
