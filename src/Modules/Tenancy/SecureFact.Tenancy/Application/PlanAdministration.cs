using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;
using SecureFact.Tenancy.Contracts;
using SecureFact.Tenancy.Domain;
using SecureFact.Tenancy.Infrastructure;

namespace SecureFact.Tenancy.Application;

internal sealed partial class PlanAdministration(TenancyDbContext db, IDataScope scope, TimeProvider clock, IAuditTrail audit) : IPlanAdministration
{
    private const int MaxLimit = 100_000_000;

    internal static readonly Error PlanMissing = Error.NotFound(ErrorCodes.PlanNotFound, "Plan no encontrado", "El plan no existe.");

    [GeneratedRegex("^[a-z][a-z0-9-]{1,39}$")]
    private static partial Regex CodePattern();

    public async Task<Result<IReadOnlyList<PlanDto>>> ListAsync(CancellationToken cancellationToken)
    {
        if (RequirePlatform() is { } denied)
        {
            return denied;
        }

        return (await db.Plans.AsNoTracking().OrderBy(p => p.Name).ThenBy(p => p.Code).ToListAsync(cancellationToken)).Select(ToDto).ToList();
    }

    public async Task<Result<PlanDto>> CreateAsync(PlanInput input, CancellationToken cancellationToken)
    {
        if (RequirePlatform() is { } denied)
        {
            return denied;
        }

        var code = input.Code?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!CodePattern().IsMatch(code))
        {
            return Error.Validation(ErrorCodes.InvalidPlan, "Código de plan inválido", "El código usa 2 a 40 letras minúsculas, dígitos o guiones y empieza con una letra.");
        }

        if (Validate(input) is { } invalid)
        {
            return invalid;
        }

        if (await db.Plans.AnyAsync(p => p.Code == code, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.PlanCodeInUse, "Código en uso", "Ya existe un plan con ese código.");
        }

        if (await CheckResellerAsync(input.ResellerId, cancellationToken) is { } badReseller)
        {
            return badReseller;
        }

        var plan = Plan.Create(Guid.CreateVersion7(), code, input.Name.Trim(), input.MaxCompanies, input.MaxUsers, input.MaxDocumentsPerMonth, input.ResellerId, clock.GetUtcNow());
        db.Plans.Add(plan);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.PlanCreated, "plan", plan.Id.ToString("D"), null, NewValues: Values(plan)), cancellationToken);
        return ToDto(plan);
    }

    public async Task<Result<PlanDto>> UpdateAsync(Guid id, PlanInput input, CancellationToken cancellationToken)
    {
        if (RequirePlatform() is { } denied)
        {
            return denied;
        }

        if (Validate(input) is { } invalid)
        {
            return invalid;
        }

        var plan = await db.Plans.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (plan is null)
        {
            return PlanMissing;
        }

        if (await CheckResellerAsync(input.ResellerId, cancellationToken) is { } badReseller)
        {
            return badReseller;
        }

        var before = Values(plan);
        plan.Update(input.Name.Trim(), input.MaxCompanies, input.MaxUsers, input.MaxDocumentsPerMonth, input.ResellerId, input.IsActive);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.PlanUpdated, "plan", plan.Id.ToString("D"), null, OldValues: before, NewValues: Values(plan)), cancellationToken);
        return ToDto(plan);
    }

    public async Task<Result<TenantDto>> AssignAsync(TenantId tenantId, Guid planId, CancellationToken cancellationToken)
    {
        if (RequirePlatform() is { } denied)
        {
            return denied;
        }

        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId.Value, cancellationToken);
        if (tenant is null)
        {
            return Error.NotFound(ErrorCodes.TenantNotFound, "Tenant no encontrado", "El tenant no existe o no es visible para este contexto.");
        }

        var plan = await db.Plans.AsNoTracking().SingleOrDefaultAsync(p => p.Id == planId, cancellationToken);
        if (plan is null)
        {
            return PlanMissing;
        }

        if (!plan.IsActive && tenant.PlanId != plan.Id)
        {
            return Error.Validation(ErrorCodes.InvalidPlan, "Plan inactivo", "Un plan inactivo no se puede asignar a un tenant.");
        }

        var previous = tenant.PlanId;
        tenant.ChangePlan(plan.Id);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.TenantPlanChanged, "tenant", tenant.Id.ToString("D"), tenant.Id,
                OldValues: new Dictionary<string, object?> { ["planId"] = previous },
                NewValues: new Dictionary<string, object?> { ["planId"] = plan.Id, ["plan"] = plan.Code }),
            cancellationToken);
        return TenantAdministration.ToDto(tenant);
    }

    internal static PlanDto ToDto(Plan p) => new(p.Id, p.Code, p.Name, p.MaxCompanies, p.MaxUsers, p.MaxDocumentsPerMonth, p.IsActive, p.ResellerId);

    private static Error? Validate(PlanInput input)
    {
        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 80)
        {
            return Error.Validation(ErrorCodes.InvalidPlan, "Nombre de plan inválido", "El nombre debe tener entre 2 y 80 caracteres.");
        }

        foreach (var limit in new[] { input.MaxCompanies, input.MaxUsers, input.MaxDocumentsPerMonth })
        {
            if (limit is < 0 or > MaxLimit)
            {
                return Error.Validation(ErrorCodes.InvalidPlan, "Límite inválido", $"Cada límite es un entero entre 0 y {MaxLimit}, o vacío para ilimitado.");
            }
        }

        return null;
    }

    private static Dictionary<string, object?> Values(Plan p) => new()
    {
        ["code"] = p.Code,
        ["name"] = p.Name,
        ["maxCompanies"] = p.MaxCompanies,
        ["maxUsers"] = p.MaxUsers,
        ["maxDocumentsPerMonth"] = p.MaxDocumentsPerMonth,
        ["isActive"] = p.IsActive,
        ["resellerId"] = p.ResellerId,
    };

    private async Task<Error?> CheckResellerAsync(Guid? resellerId, CancellationToken cancellationToken) =>
        resellerId is { } id && !await db.Resellers.AnyAsync(r => r.Id == id, cancellationToken)
            ? Error.Validation(ErrorCodes.InvalidPlan, "Revendedor inexistente", "El revendedor del plan no existe.")
            : null;

    private Error? RequirePlatform() =>
        scope.Kind == DataScopeKind.Platform ? null : Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Solo la plataforma administra los planes.");
}
