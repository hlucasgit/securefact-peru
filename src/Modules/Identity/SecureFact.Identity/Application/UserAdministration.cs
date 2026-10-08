using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Identity.Domain;
using SecureFact.Identity.Infrastructure;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Identity.Application;

internal sealed class UserAdministration(
    IdentityDbContext db,
    ICurrentUser actor,
    PasswordHasher hasher,
    TimeProvider clock,
    IAuditTrail audit,
    IPlanLimits plans,
    IResellerAdministration resellers,
    IAccountNotices notices) : IUserAdministration
{
    private const int MaxPageSize = 200;

    private static readonly Error NotFound = Error.NotFound(ErrorCodes.UserNotFound, "Usuario no encontrado", "El usuario no existe o no es visible para este contexto.");

    public async Task<Result<UserDto>> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim() ?? string.Empty;
        if (!IsValidEmail(email))
        {
            return Error.Validation(ErrorCodes.InvalidEmail, "Correo inválido", "Ingrese un correo electrónico válido.");
        }

        var name = request.DisplayName?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 120)
        {
            return Error.Validation(ErrorCodes.InvalidRequest, "Nombre inválido", "El nombre debe tener entre 2 y 120 caracteres.");
        }

        if (PasswordPolicy.Validate(request.Password, email) is { } weak)
        {
            return weak;
        }

        var roles = (request.Roles ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (roles.Count == 0)
        {
            return Error.Validation(ErrorCodes.UnknownRole, "Rol requerido", "Indique al menos un rol.");
        }

        var tenantResult = ResolveTargetTenant(request.TenantId, roles, request.ResellerId);
        if (!tenantResult.IsSuccess)
        {
            return tenantResult.Error;
        }

        foreach (var role in roles)
        {
            if (CheckAssignable(role) is { } denied)
            {
                return denied;
            }
        }

        if (await CheckResellerOfUserAsync(roles, request.ResellerId, cancellationToken) is { } badReseller)
        {
            return badReseller;
        }

        var normalized = User.Normalize(email);
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.EmailNormalized == normalized, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.EmailInUse, "Correo en uso", "Ya existe un usuario con ese correo.");
        }

        if (tenantResult.Value is { } owner)
        {
            var allowed = await plans.EnsureCanAddAsync(
                new TenantId(owner), PlanResource.Users, await db.Users.CountAsync(u => u.TenantId == owner && u.IsActive, cancellationToken), cancellationToken);
            if (!allowed.IsSuccess)
            {
                return allowed.Error;
            }
        }

        var now = clock.GetUtcNow();
        var user = User.Create(Guid.CreateVersion7(), tenantResult.Value, email, name, hasher.Hash(request.Password), now, request.ResellerId);
        foreach (var role in roles)
        {
            user.AddRole(role, actor.UserId, now);
        }

        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        await RecordAsync(AuditActions.UserCreated, user, new Dictionary<string, object?> { ["email"] = user.Email, ["roles"] = roles }, cancellationToken);
        await notices.AccountCreatedAsync(new AccountCreatedNotice(user.Email, user.DisplayName, user.TenantId, user.ResellerId, roles), cancellationToken);
        return ToDto(user);
    }

    public async Task<Result<UserDto>> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Roles).SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null ? NotFound : ToDto(user);
    }

    public async Task<IReadOnlyList<UserDto>> ListAsync(int skip, int take, Guid? tenantId, CancellationToken cancellationToken)
    {
        var query = db.Users.AsNoTracking().Include(u => u.Roles).AsQueryable();
        if (tenantId is { } only)
        {
            // A tenant user never sees another tenant (the filter of the data scope already says so); platform staff narrow the list to one tenant.
            query = query.Where(u => u.TenantId == only);
        }

        var users = await query
            .OrderBy(u => u.EmailNormalized)
            .Skip(Math.Max(skip, 0))
            .Take(Math.Clamp(take, 1, MaxPageSize))
            .ToListAsync(cancellationToken);
        return users.Select(ToDto).ToList();
    }

    public async Task<Result<UserDto>> CreateTenantOwnerAsync(Guid tenantId, string email, string displayName, string password, CancellationToken cancellationToken)
    {
        if (!actor.IsPlatform && actor.ResellerId is null)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Solo la plataforma o un revendedor crea el propietario de una cuenta nueva.");
        }

        var mail = email?.Trim() ?? string.Empty;
        if (!IsValidEmail(mail))
        {
            return Error.Validation(ErrorCodes.InvalidEmail, "Correo inválido", "Ingrese un correo electrónico válido.");
        }

        var name = displayName?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 120)
        {
            return Error.Validation(ErrorCodes.InvalidRequest, "Nombre inválido", "El nombre debe tener entre 2 y 120 caracteres.");
        }

        if (PasswordPolicy.Validate(password, mail) is { } weak)
        {
            return weak;
        }

        var normalized = User.Normalize(mail);
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.EmailNormalized == normalized, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.EmailInUse, "Correo en uso", "Ya existe un usuario con ese correo.");
        }

        var allowed = await plans.EnsureCanAddAsync(
            new TenantId(tenantId), PlanResource.Users, await db.Users.CountAsync(u => u.TenantId == tenantId && u.IsActive, cancellationToken), cancellationToken);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        var now = clock.GetUtcNow();
        var user = User.Create(Guid.CreateVersion7(), tenantId, mail, name, hasher.Hash(password), now);
        user.AddRole(Roles.TenantOwner, actor.UserId, now);
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        await RecordAsync(AuditActions.UserCreated, user, new Dictionary<string, object?> { ["email"] = user.Email, ["roles"] = new[] { Roles.TenantOwner } }, cancellationToken);
        await notices.AccountCreatedAsync(new AccountCreatedNotice(user.Email, user.DisplayName, user.TenantId, user.ResellerId, [Roles.TenantOwner]), cancellationToken);
        return ToDto(user);
    }

    public async Task<int> CountActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await db.Users.CountAsync(u => u.TenantId == tenantId && u.IsActive, cancellationToken);

    public async Task<Result<UserDto>> AssignRoleAsync(Guid userId, string role, CancellationToken cancellationToken)
    {
        var user = await db.Users.Include(u => u.Roles).SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return NotFound;
        }

        if (CheckAssignable(role) is { } denied)
        {
            return denied;
        }

        var levelCheck = ResolveTargetTenant(user.TenantId, [.. user.Roles.Select(r => r.RoleCode), role], user.ResellerId);
        if (!levelCheck.IsSuccess)
        {
            return levelCheck.Error;
        }

        if (user.Roles.All(r => r.RoleCode != role))
        {
            db.UserRoles.Add(user.AddRole(role, actor.UserId, clock.GetUtcNow()));
            await db.SaveChangesAsync(cancellationToken);
            await RecordAsync(AuditActions.UserRoleAssigned, user, new Dictionary<string, object?> { ["role"] = role }, cancellationToken);
        }

        return ToDto(user);
    }

    public async Task<Result<UserDto>> RemoveRoleAsync(Guid userId, string role, CancellationToken cancellationToken)
    {
        var user = await db.Users.Include(u => u.Roles).SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return NotFound;
        }

        if (CheckAssignable(role) is { } denied)
        {
            return denied;
        }

        var assignment = user.Roles.SingleOrDefault(r => r.RoleCode == role);
        if (assignment is null)
        {
            return ToDto(user);
        }

        if (user.Roles.Count == 1)
        {
            return Error.Validation(ErrorCodes.UnknownRole, "Rol requerido", "El usuario debe conservar al menos un rol.");
        }

        if (role == Roles.TenantOwner && user.TenantId is { } tenantId
            && !await db.UserRoles.AnyAsync(r => r.TenantId == tenantId && r.RoleCode == Roles.TenantOwner && r.UserId != userId, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.RoleNotAssignable, "Rol no removible", "No se puede quitar el último propietario del tenant.");
        }

        db.UserRoles.Remove(assignment);
        await db.SaveChangesAsync(cancellationToken);
        await RecordAsync(AuditActions.UserRoleRemoved, user, new Dictionary<string, object?> { ["role"] = role }, cancellationToken);
        var refreshed = await db.Users.AsNoTracking().Include(u => u.Roles).SingleAsync(u => u.Id == userId, cancellationToken);
        return ToDto(refreshed);
    }

    public async Task<Result<Unit>> DeactivateAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (actor.UserId == userId)
        {
            return Error.Validation(ErrorCodes.RoleNotAssignable, "Operación no permitida", "No puede desactivar su propia cuenta.");
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return NotFound;
        }

        var now = clock.GetUtcNow();
        user.Deactivate(now);
        await RevokeAllSessionsAsync(userId, "deactivated", now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await RecordAsync(AuditActions.UserDeactivated, user, null, cancellationToken);
        return Unit.Value;
    }

    public async Task<Result<Unit>> RevokeSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return NotFound;
        }

        await RevokeAllSessionsAsync(userId, "admin-revoked", clock.GetUtcNow(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await RecordAsync(AuditActions.SessionsRevoked, user, null, cancellationToken);
        return Unit.Value;
    }

    private Task RecordAsync(string action, User target, IReadOnlyDictionary<string, object?>? values, CancellationToken cancellationToken) =>
        audit.RecordAsync(new AuditEvent(action, "user", target.Id.ToString("D"), target.TenantId, NewValues: values), cancellationToken);

    private async Task RevokeAllSessionsAsync(Guid userId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sessions = await db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.Revoke(reason, now);
        }
    }

    /// <summary>Platform and reseller roles cannot be mixed with tenant roles. Tenant users always belong to the actor's tenant.</summary>
    private Result<Guid?> ResolveTargetTenant(Guid? requestedTenant, IReadOnlyCollection<string> roles, Guid? resellerId)
    {
        var unknown = roles.FirstOrDefault(r => !RoleCatalog.Exists(r));
        if (unknown is not null)
        {
            return Error.Validation(ErrorCodes.UnknownRole, "Rol desconocido", $"El rol '{unknown}' no existe.");
        }

        var levels = roles.Select(RoleCatalog.LevelOf).Distinct().ToList();
        if (levels.Count > 1)
        {
            return Error.Validation(ErrorCodes.RoleNotAssignable, "Roles incompatibles", "No se pueden combinar roles de plataforma y de tenant.");
        }

        if (levels[0] == RoleLevel.Reseller)
        {
            // A reseller user belongs to no tenant and to exactly one reseller, named when the user is created.
            return actor.IsPlatform && requestedTenant is null && resellerId is not null
                ? (Guid?)null
                : Error.Forbidden(ErrorCodes.RoleNotAssignable, "Rol no asignable", "Solo el personal de plataforma crea usuarios de un revendedor, indicando el revendedor.");
        }

        if (levels[0] == RoleLevel.Platform)
        {
            return actor.IsPlatform && requestedTenant is null
                ? (Guid?)null
                : Error.Forbidden(ErrorCodes.RoleNotAssignable, "Rol no asignable", "Solo el personal de plataforma puede crear usuarios de plataforma.");
        }

        if (actor.IsPlatform)
        {
            return requestedTenant is { } tenant
                ? tenant
                : Error.Validation(ErrorCodes.InvalidRequest, "Tenant requerido", "Indique el tenant del usuario.");
        }

        if (actor.TenantId is not { } own || (requestedTenant is { } other && other != own.Value))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "No puede administrar usuarios de otro tenant.");
        }

        return own.Value;
    }

    /// <summary>A reseller id goes with the ResellerAdmin role and with nothing else, and it must name a reseller that exists and is on.</summary>
    private async Task<Error?> CheckResellerOfUserAsync(IReadOnlyCollection<string> roles, Guid? resellerId, CancellationToken cancellationToken)
    {
        var isResellerUser = roles.Any(r => RoleCatalog.LevelOf(r) == RoleLevel.Reseller);
        if (!isResellerUser)
        {
            return resellerId is null ? null : Error.Validation(ErrorCodes.InvalidRequest, "Revendedor no permitido", "El revendedor solo se indica para el rol ResellerAdmin.");
        }

        return resellerId is { } id && await resellers.IsActiveAsync(id, cancellationToken)
            ? null
            : Error.NotFound(ErrorCodes.ResellerNotFound, "Revendedor no encontrado", "El revendedor no existe o está desactivado.");
    }

    private Error? CheckAssignable(string role) =>
        RoleCatalog.CanAssign(actor.Roles, actor.IsPlatform, role)
            ? null
            : Error.Forbidden(ErrorCodes.RoleNotAssignable, "Rol no asignable", $"No tiene permisos suficientes para gestionar el rol '{role}'.");

    private static bool IsValidEmail(string email)
    {
        if (email.Length is < 5 or > 254 || !MailAddress.TryCreate(email, out var parsed))
        {
            return false;
        }

        return string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase) && parsed.Host.Contains('.', StringComparison.Ordinal);
    }

    private static UserDto ToDto(User u) => new(
        u.Id, u.TenantId, u.ResellerId, u.Email, u.DisplayName, [.. u.Roles.Select(r => r.RoleCode).Order(StringComparer.Ordinal)],
        u.IsActive, u.MfaEnabled, u.CreatedAt);
}
