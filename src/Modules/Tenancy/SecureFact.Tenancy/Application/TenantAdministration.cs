using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SecureFact.Audit.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;
using SecureFact.Tenancy.Contracts;
using SecureFact.Tenancy.Domain;
using SecureFact.Tenancy.Infrastructure;

namespace SecureFact.Tenancy.Application;

internal sealed class TenantAdministration(TenancyDbContext db, IDataScope scope, TimeProvider clock, IAuditTrail audit, IMemoryCache cache) : ITenantAdministration
{
    private const int MinNameLength = 3;
    private const int MaxNameLength = 120;

    public async Task<Result<TenantDto>> CreateAsync(CreateTenantRequest request, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Platform)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Solo la plataforma puede crear tenants.");
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < MinNameLength or > MaxNameLength)
        {
            return Error.Validation(
                ErrorCodes.InvalidTenantName,
                "Nombre de tenant inválido",
                $"El nombre debe tener entre {MinNameLength} y {MaxNameLength} caracteres.");
        }

        if (request.ResellerId is { } resellerId && !await db.Resellers.AnyAsync(r => r.Id == resellerId, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.ResellerNotFound, "Revendedor no encontrado", "El revendedor no existe.");
        }

        // A tenant starts on the plan that was asked for, or on the default one (which limits nothing).
        var planId = request.PlanId ?? Plan.DefaultId;
        var plan = await db.Plans.AsNoTracking().SingleOrDefaultAsync(p => p.Id == planId, cancellationToken);
        if (plan is null)
        {
            return PlanAdministration.PlanMissing;
        }

        if (!plan.IsActive)
        {
            return Error.Validation(ErrorCodes.InvalidPlan, "Plan inactivo", "Un plan inactivo no se puede asignar a un tenant.");
        }

        var tenant = Tenant.Create(TenantId.New().Value, name, request.Environment, request.ResellerId, plan.Id, clock.GetUtcNow());
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.TenantCreated, "tenant", tenant.Id.ToString("D"), tenant.Id,
                NewValues: new Dictionary<string, object?> { ["name"] = tenant.Name, ["environment"] = tenant.Environment, ["resellerId"] = tenant.ResellerId, ["plan"] = plan.Code }),
            cancellationToken);
        return ToDto(tenant);
    }

    public async Task<Result<TenantDto>> GetAsync(TenantId id, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id.Value, cancellationToken);
        return tenant is null
            ? Error.NotFound(ErrorCodes.TenantNotFound, "Tenant no encontrado", "El tenant no existe o no es visible para este contexto.")
            : ToDto(tenant);
    }

    private const int MaxPageSize = 100;
    private const int MinReasonLength = 3;
    private const int MaxReasonLength = 300;

    public async Task<Result<IReadOnlyList<TenantDto>>> ListAsync(string? search, TenantStatus? status, int skip, int take, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Platform)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Solo la plataforma puede listar los tenants.");
        }

        var query = db.Tenants.AsNoTracking().AsQueryable();
        if (status is { } wanted)
        {
            query = query.Where(t => t.Status == wanted);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(t => EF.Functions.ILike(t.Name, $"%{EscapeLike(search.Trim())}%", "\\"));
        }

        var tenants = await query.OrderBy(t => t.Name).ThenBy(t => t.Id).Skip(Math.Max(skip, 0)).Take(Math.Clamp(take, 1, MaxPageSize)).ToListAsync(cancellationToken);
        return tenants.Select(ToDto).ToList();
    }

    public async Task<Result<TenantDto>> ChangeStatusAsync(TenantId id, TenantStatus target, string reason, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Platform)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Solo la plataforma puede cambiar el estado de un tenant.");
        }

        var why = reason?.Trim() ?? string.Empty;
        if (why.Length is < MinReasonLength or > MaxReasonLength)
        {
            return Error.Validation(ErrorCodes.InvalidTenantStatusChange, "Motivo inválido", $"El motivo debe tener entre {MinReasonLength} y {MaxReasonLength} caracteres.");
        }

        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == id.Value, cancellationToken);
        if (tenant is null)
        {
            return Error.NotFound(ErrorCodes.TenantNotFound, "Tenant no encontrado", "El tenant no existe o no es visible para este contexto.");
        }

        // Active <-> Suspended, and either to Closed; a closed tenant is final (its documents and its audit trail stay).
        var allowed = (tenant.Status, target) switch
        {
            (TenantStatus.Active, TenantStatus.Suspended) => true,
            (TenantStatus.Suspended, TenantStatus.Active) => true,
            (TenantStatus.Active or TenantStatus.Suspended, TenantStatus.Closed) => true,
            _ => false,
        };
        if (!allowed)
        {
            return Error.Validation(ErrorCodes.InvalidTenantStatusChange, "Cambio de estado inválido", $"Un tenant {StatusName(tenant.Status)} no puede pasar a {StatusName(target)}.");
        }

        var previous = tenant.Status;
        tenant.ChangeStatus(target);
        await db.SaveChangesAsync(cancellationToken);
        cache.Remove(TenantStatusReader.Key(tenant.Id));
        var action = target switch { TenantStatus.Suspended => AuditActions.TenantSuspended, TenantStatus.Closed => AuditActions.TenantClosed, _ => AuditActions.TenantReactivated };
        await audit.RecordAsync(
            new AuditEvent(
                action, "tenant", tenant.Id.ToString("D"), tenant.Id,
                OldValues: new Dictionary<string, object?> { ["status"] = previous },
                NewValues: new Dictionary<string, object?> { ["status"] = target, ["reason"] = why }),
            cancellationToken);
        return ToDto(tenant);
    }

    private static string StatusName(TenantStatus status) => status switch { TenantStatus.Active => "activo", TenantStatus.Suspended => "suspendido", _ => "cerrado" };

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    internal static TenantDto ToDto(Tenant t) => new(new TenantId(t.Id), t.Name, t.Status, t.Environment, t.ResellerId, t.CreatedAt, t.PlanId);
}
