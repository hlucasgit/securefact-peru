using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SecureFact.Audit.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Tenancy.Contracts;
using SecureFact.Tenancy.Domain;
using SecureFact.Tenancy.Infrastructure;

namespace SecureFact.Tenancy.Application;

internal sealed class ResellerAdministration(TenancyDbContext db, IDataScope scope, ICurrentUser actor, TimeProvider clock, IAuditTrail audit, IMemoryCache cache, ITenantNotices notices) : IResellerAdministration
{
    private const int MaxPageSize = 100;
    private const int MinNameLength = 3;
    private const int MaxNameLength = 120;
    private const int MinReasonLength = 3;
    private const int MaxReasonLength = 300;

    private static readonly Error ResellerMissing = Error.NotFound(ErrorCodes.ResellerNotFound, "Revendedor no encontrado", "El revendedor no existe.");

    // The same answer for a tenant that does not exist and for one of another reseller: a reseller cannot probe for the tenants of the others.
    private static readonly Error TenantMissing = Error.NotFound(ErrorCodes.TenantNotFound, "Tenant no encontrado", "El tenant no existe o no es visible para este contexto.");

    // ---------- platform staff ----------

    public async Task<int> CountActiveTenantsAsync(Guid resellerId, CancellationToken cancellationToken) =>
        scope.Kind == DataScopeKind.Platform
            ? await db.Tenants.AsNoTracking().CountAsync(t => t.ResellerId == resellerId && t.Status == TenantStatus.Active, cancellationToken)
            : 0;

    public async Task<Result<IReadOnlyList<ResellerDto>>> ListAsync(CancellationToken cancellationToken)
    {
        if (RequirePlatformStaff() is { } denied)
        {
            return denied;
        }

        var resellers = await db.Resellers.AsNoTracking().OrderBy(r => r.Name).ThenBy(r => r.Id).ToListAsync(cancellationToken);
        var counts = await db.Tenants.AsNoTracking().Where(t => t.ResellerId != null).GroupBy(t => t.ResellerId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        return resellers.Select(r => ToDto(r, counts.FirstOrDefault(c => c.Key == r.Id)?.Count ?? 0)).ToList();
    }

    public async Task<Result<ResellerDto>> CreateAsync(string name, CancellationToken cancellationToken)
    {
        if (RequirePlatformStaff() is { } denied)
        {
            return denied;
        }

        var clean = name?.Trim() ?? string.Empty;
        if (clean.Length is < MinNameLength or > MaxNameLength)
        {
            return Error.Validation(ErrorCodes.InvalidReseller, "Nombre de revendedor inválido", $"El nombre debe tener entre {MinNameLength} y {MaxNameLength} caracteres.");
        }

        var reseller = Reseller.Create(Guid.CreateVersion7(), clean, clock.GetUtcNow());
        db.Resellers.Add(reseller);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.ResellerCreated, "reseller", reseller.Id.ToString("D"), null, NewValues: Values(reseller)), cancellationToken);
        return ToDto(reseller, 0);
    }

    public async Task<Result<ResellerDto>> UpdateAsync(Guid id, string name, bool isActive, CancellationToken cancellationToken)
    {
        if (RequirePlatformStaff() is { } denied)
        {
            return denied;
        }

        var clean = name?.Trim() ?? string.Empty;
        if (clean.Length is < MinNameLength or > MaxNameLength)
        {
            return Error.Validation(ErrorCodes.InvalidReseller, "Nombre de revendedor inválido", $"El nombre debe tener entre {MinNameLength} y {MaxNameLength} caracteres.");
        }

        var reseller = await db.Resellers.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (reseller is null)
        {
            return ResellerMissing;
        }

        var before = Values(reseller);
        reseller.Update(clean, isActive);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.ResellerUpdated, "reseller", reseller.Id.ToString("D"), null, OldValues: before, NewValues: Values(reseller)), cancellationToken);
        return ToDto(reseller, await db.Tenants.CountAsync(t => t.ResellerId == id, cancellationToken));
    }

    public async Task<Result<TenantDto>> AssignTenantAsync(TenantId tenantId, Guid? resellerId, CancellationToken cancellationToken)
    {
        if (RequirePlatformStaff() is { } denied)
        {
            return denied;
        }

        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId.Value, cancellationToken);
        if (tenant is null)
        {
            return TenantMissing;
        }

        if (resellerId is { } id && !await db.Resellers.AnyAsync(r => r.Id == id, cancellationToken))
        {
            return ResellerMissing;
        }

        var previous = tenant.ResellerId;
        tenant.AssignReseller(resellerId, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.TenantResellerChanged, "tenant", tenant.Id.ToString("D"), tenant.Id,
                OldValues: new Dictionary<string, object?> { ["resellerId"] = previous },
                NewValues: new Dictionary<string, object?> { ["resellerId"] = resellerId }),
            cancellationToken);
        return TenantAdministration.ToDto(tenant);
    }

    public async Task<bool> IsActiveAsync(Guid resellerId, CancellationToken cancellationToken) =>
        await db.Resellers.AsNoTracking().AnyAsync(r => r.Id == resellerId && r.IsActive, cancellationToken);

    // ---------- the reseller's own view ----------

    public async Task<Result<ResellerDto>> GetOwnAsync(Guid resellerId, CancellationToken cancellationToken)
    {
        if (RequireReseller(resellerId) is { } denied)
        {
            return denied;
        }

        var reseller = await db.Resellers.AsNoTracking().SingleOrDefaultAsync(r => r.Id == resellerId, cancellationToken);
        return reseller is null ? ResellerMissing : ToDto(reseller, await db.Tenants.CountAsync(t => t.ResellerId == resellerId, cancellationToken));
    }

    public async Task<Result<IReadOnlyList<TenantDto>>> ListTenantsAsync(Guid resellerId, string? search, int skip, int take, CancellationToken cancellationToken)
    {
        if (RequireReseller(resellerId) is { } denied)
        {
            return denied;
        }

        var query = db.Tenants.AsNoTracking().Where(t => t.ResellerId == resellerId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(t => EF.Functions.ILike(t.Name, $"%{EscapeLike(search.Trim())}%", "\\"));
        }

        var tenants = await query.OrderBy(t => t.Name).ThenBy(t => t.Id).Skip(Math.Max(skip, 0)).Take(Math.Clamp(take, 1, MaxPageSize)).ToListAsync(cancellationToken);
        return tenants.Select(TenantAdministration.ToDto).ToList();
    }

    public async Task<Result<TenantDto>> GetTenantAsync(Guid resellerId, TenantId tenantId, CancellationToken cancellationToken)
    {
        if (RequireReseller(resellerId) is { } denied)
        {
            return denied;
        }

        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId.Value && t.ResellerId == resellerId, cancellationToken);
        return tenant is null ? TenantMissing : TenantAdministration.ToDto(tenant);
    }

    public async Task<Result<TenantDto>> CreateTenantAsync(Guid resellerId, ResellerTenantRequest request, CancellationToken cancellationToken)
    {
        if (RequireReseller(resellerId) is { } denied)
        {
            return denied;
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < MinNameLength or > MaxNameLength)
        {
            return Error.Validation(ErrorCodes.InvalidTenantName, "Nombre de tenant inválido", $"El nombre debe tener entre {MinNameLength} y {MaxNameLength} caracteres.");
        }

        var plan = await AllowedPlanAsync(resellerId, request.PlanId ?? Plan.DefaultId, null, cancellationToken);
        if (!plan.IsSuccess)
        {
            return plan.Error;
        }

        var tenant = Tenant.Create(TenantId.New().Value, name, request.Environment, resellerId, plan.Value.Id, clock.GetUtcNow());
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.TenantCreated, "tenant", tenant.Id.ToString("D"), tenant.Id,
                NewValues: new Dictionary<string, object?> { ["name"] = tenant.Name, ["environment"] = tenant.Environment, ["resellerId"] = resellerId, ["plan"] = plan.Value.Code }),
            cancellationToken);
        return TenantAdministration.ToDto(tenant);
    }

    public async Task<Result<IReadOnlyList<PlanDto>>> ListPlansAsync(Guid resellerId, CancellationToken cancellationToken)
    {
        if (RequireReseller(resellerId) is { } denied)
        {
            return denied;
        }

        var plans = await db.Plans.AsNoTracking().Where(p => p.IsActive && (p.ResellerId == null || p.ResellerId == resellerId)).OrderBy(p => p.Name).ThenBy(p => p.Code).ToListAsync(cancellationToken);
        return plans.Select(PlanAdministration.ToDto).ToList();
    }

    public async Task<Result<TenantDto>> AssignPlanAsync(Guid resellerId, TenantId tenantId, Guid planId, CancellationToken cancellationToken)
    {
        if (RequireReseller(resellerId) is { } denied)
        {
            return denied;
        }

        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId.Value && t.ResellerId == resellerId, cancellationToken);
        if (tenant is null)
        {
            return TenantMissing;
        }

        var plan = await AllowedPlanAsync(resellerId, planId, tenant.PlanId, cancellationToken);
        if (!plan.IsSuccess)
        {
            return plan.Error;
        }

        var previous = tenant.PlanId;
        tenant.ChangePlan(plan.Value.Id, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.TenantPlanChanged, "tenant", tenant.Id.ToString("D"), tenant.Id,
                OldValues: new Dictionary<string, object?> { ["planId"] = previous },
                NewValues: new Dictionary<string, object?> { ["planId"] = plan.Value.Id, ["plan"] = plan.Value.Code, ["byReseller"] = resellerId }),
            cancellationToken);
        return TenantAdministration.ToDto(tenant);
    }

    public async Task<Result<TenantDto>> ChangeTenantStatusAsync(Guid resellerId, TenantId tenantId, TenantStatus target, string reason, CancellationToken cancellationToken)
    {
        if (RequireReseller(resellerId) is { } denied)
        {
            return denied;
        }

        var why = reason?.Trim() ?? string.Empty;
        if (why.Length is < MinReasonLength or > MaxReasonLength)
        {
            return Error.Validation(ErrorCodes.InvalidTenantStatusChange, "Motivo inválido", $"El motivo debe tener entre {MinReasonLength} y {MaxReasonLength} caracteres.");
        }

        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId.Value && t.ResellerId == resellerId, cancellationToken);
        if (tenant is null)
        {
            return TenantMissing;
        }

        // A reseller suspends and reactivates; closing an account is final and is the platform's.
        if (target == TenantStatus.Closed)
        {
            return Error.Validation(ErrorCodes.InvalidTenantStatusChange, "Cambio de estado inválido", "Un revendedor suspende y reactiva cuentas; el cierre definitivo lo hace la plataforma.");
        }

        if (tenant.Status == TenantStatus.Suspended && tenant.SuspendedBy != SuspensionSource.Reseller && target == TenantStatus.Active)
        {
            return Error.Forbidden(ErrorCodes.SuspensionNotYours, "Suspensión de la plataforma", "La plataforma suspendió esta cuenta y solo ella puede reactivarla. Comuníquese con soporte.");
        }

        var allowed = (tenant.Status, target) switch
        {
            (TenantStatus.Active, TenantStatus.Suspended) => true,
            (TenantStatus.Suspended, TenantStatus.Active) => true,
            _ => false,
        };
        if (!allowed)
        {
            return Error.Validation(ErrorCodes.InvalidTenantStatusChange, "Cambio de estado inválido", $"Una cuenta {StatusName(tenant.Status)} no puede pasar a {StatusName(target)}.");
        }

        var previous = tenant.Status;
        tenant.ChangeStatus(target, SuspensionSource.Reseller);
        await db.SaveChangesAsync(cancellationToken);
        cache.Remove(TenantStatusReader.Key(tenant.Id));
        await audit.RecordAsync(
            new AuditEvent(
                target == TenantStatus.Suspended ? AuditActions.TenantSuspended : AuditActions.TenantReactivated, "tenant", tenant.Id.ToString("D"), tenant.Id,
                OldValues: new Dictionary<string, object?> { ["status"] = previous },
                NewValues: new Dictionary<string, object?> { ["status"] = target, ["reason"] = why, ["suspendedBy"] = tenant.SuspendedBy, ["byReseller"] = resellerId }),
            cancellationToken);
        await notices.StatusChangedAsync(new TenantStatusNotice(tenant.Id, tenant.Name, tenant.ResellerId, target), cancellationToken);
        return TenantAdministration.ToDto(tenant);
    }

    private static string StatusName(TenantStatus status) => status switch { TenantStatus.Active => "activa", TenantStatus.Suspended => "suspendida", _ => "cerrada" };

    // ---------- rules ----------

    /// <summary>A reseller assigns an active plan of the public catalogue or one of its own private plans, never another reseller's. A tenant may keep the plan it already has.</summary>
    private async Task<Result<Plan>> AllowedPlanAsync(Guid resellerId, Guid planId, Guid? currentPlanId, CancellationToken cancellationToken)
    {
        var plan = await db.Plans.AsNoTracking().SingleOrDefaultAsync(p => p.Id == planId, cancellationToken);
        if (plan is null || (plan.ResellerId is { } owner && owner != resellerId))
        {
            return PlanAdministration.PlanMissing;
        }

        return !plan.IsActive && plan.Id != currentPlanId
            ? Error.Validation(ErrorCodes.InvalidPlan, "Plan inactivo", "Un plan inactivo no se puede asignar a un tenant.")
            : plan;
    }

    private Error? RequirePlatformStaff() =>
        scope.Kind == DataScopeKind.Platform && actor.IsPlatform
            ? null
            : Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Solo el personal de la plataforma administra los revendedores.");

    /// <summary>The reseller comes from the token; a request can name only its own.</summary>
    private Error? RequireReseller(Guid resellerId) =>
        scope.Kind == DataScopeKind.Platform && actor.ResellerId == resellerId
            ? null
            : Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Esta operación es del revendedor.");

    private static Dictionary<string, object?> Values(Reseller r) => new() { ["name"] = r.Name, ["isActive"] = r.IsActive };

    private static ResellerDto ToDto(Reseller r, int tenants) => new(r.Id, r.Name, r.IsActive, tenants, r.CreatedAt);

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
