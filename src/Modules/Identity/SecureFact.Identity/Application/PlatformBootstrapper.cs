using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Identity.Domain;
using SecureFact.Identity.Infrastructure;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Identity.Application;

internal sealed class PlatformBootstrapper(IdentityDbContext db, DataScope scope, PasswordHasher hasher, TimeProvider clock, IAuditTrail audit) : IPlatformBootstrapper
{
    public async Task<Result<bool>> EnsureFirstPlatformAdminAsync(string email, string password, CancellationToken cancellationToken)
    {
        using var elevated = scope.Elevate("identity:bootstrap");

        if (await db.UserRoles.AnyAsync(r => r.RoleCode == Roles.PlatformSuperAdmin, cancellationToken))
        {
            return false;
        }

        if (PasswordPolicy.Validate(password, email) is { } weak)
        {
            return weak;
        }

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal))
        {
            return Error.Validation(ErrorCodes.InvalidEmail, "Correo inválido", "Ingrese un correo electrónico válido.");
        }

        var now = clock.GetUtcNow();
        var user = User.Create(Guid.CreateVersion7(), null, email, "Platform administrator", hasher.Hash(password), now);
        user.AddRole(Roles.PlatformSuperAdmin, null, now);
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.PlatformAdminBootstrapped, "user", user.Id.ToString("D"), null, ActorUserId: user.Id), cancellationToken);
        return true;
    }
}
