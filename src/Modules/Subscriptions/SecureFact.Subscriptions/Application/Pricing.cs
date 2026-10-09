using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;
using SecureFact.Subscriptions.Contracts;
using SecureFact.Subscriptions.Domain;
using SecureFact.Subscriptions.Infrastructure;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Subscriptions.Application;

internal sealed class Pricing(
    SubscriptionsDbContext db, IDataScope scope, TimeProvider clock, IAuditTrail audit, IPlanAdministration plans, ITenantAdministration tenants, IPlanLimits limits) : IPricing
{
    private const decimal MaxFee = 1_000_000m;
    private const decimal MaxUnitPrice = 10_000m;
    private const int MaxIncluded = 100_000_000;
    private const int MaxDueDays = 90;
    private const int MaxSuspendAfterDays = 365;
    private const int MaxReminderDays = 30;
    internal const int DefaultReminderDays = 3;
    private const int MaxNoteLength = 300;

    private static readonly Error PlanMissing = Error.NotFound(ErrorCodes.PlanNotFound, "Plan no encontrado", "El plan no existe.");

    public async Task<Result<IReadOnlyList<PlanPriceDto>>> ListPricesAsync(Guid planId, CancellationToken cancellationToken)
    {
        if (RequirePlatform() is { } denied)
        {
            return denied;
        }

        var versions = await db.Prices.AsNoTracking().Where(p => p.PlanId == planId).OrderBy(p => p.Version).ToListAsync(cancellationToken);
        return versions.Select(ToDto).ToList();
    }

    public async Task<Result<PlanPriceDto>> PublishPriceAsync(Guid planId, PlanPriceInput input, CancellationToken cancellationToken)
    {
        if (RequirePlatform() is { } denied)
        {
            return denied;
        }

        var plan = (await plans.ListAsync(cancellationToken)) is { IsSuccess: true } all ? all.Value.FirstOrDefault(p => p.Id == planId) : null;
        if (plan is null)
        {
            return PlanMissing;
        }

        var today = LimaCalendar.Today(clock.GetUtcNow());
        if (Validate(plan, input, today) is { } invalid)
        {
            return invalid;
        }

        // A version is added after the last one, so the history never changes and no tenant sees a price that was not in force the day it took its plan.
        var last = await db.Prices.AsNoTracking().Where(p => p.PlanId == planId).OrderByDescending(p => p.Version).FirstOrDefaultAsync(cancellationToken);
        if (last is not null && input.EffectiveFrom <= last.EffectiveFrom)
        {
            return Error.Validation(ErrorCodes.InvalidPrice, "Fecha de vigencia inválida", $"La nueva versión debe regir después de la anterior, que rige desde {last.EffectiveFrom:yyyy-MM-dd}.");
        }

        var price = PlanPrice.Create(
            planId, (last?.Version ?? 0) + 1, input.EffectiveFrom, input.MonthlyFee, plan.AllowsOverage ? input.IncludedDocuments : null, plan.AllowsOverage ? input.OverageUnitPrice : null,
            Clean(input.Note), clock.GetUtcNow());
        db.Prices.Add(price);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.PricePublished, "plan_price", price.Id.ToString("D"), null,
                NewValues: new Dictionary<string, object?>
                {
                    ["plan"] = plan.Code,
                    ["version"] = price.Version,
                    ["effectiveFrom"] = price.EffectiveFrom.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    ["monthlyFee"] = price.MonthlyFee,
                    ["includedDocuments"] = price.IncludedDocuments,
                    ["overageUnitPrice"] = price.OverageUnitPrice,
                }),
            cancellationToken);
        return ToDto(price);
    }

    public async Task<Result<IReadOnlyList<BillingPolicyDto>>> ListPoliciesAsync(CancellationToken cancellationToken)
    {
        if (RequirePlatform() is { } denied)
        {
            return denied;
        }

        var versions = await db.Policies.AsNoTracking().OrderBy(p => p.Version).ToListAsync(cancellationToken);
        return versions.Select(ToDto).ToList();
    }

    public async Task<Result<BillingPolicyDto>> PublishPolicyAsync(BillingPolicyInput input, CancellationToken cancellationToken)
    {
        if (RequirePlatform() is { } denied)
        {
            return denied;
        }

        var today = LimaCalendar.Today(clock.GetUtcNow());
        if (input.EffectiveFrom <= today)
        {
            return Error.Validation(ErrorCodes.InvalidBillingPolicy, "Fecha de vigencia inválida", "La política rige desde un día futuro: los cargos ya emitidos conservan sus fechas.");
        }

        if (input.DueDays is < 0 or > MaxDueDays)
        {
            return Error.Validation(ErrorCodes.InvalidBillingPolicy, "Plazo de pago inválido", $"El plazo de pago es de 0 a {MaxDueDays} días.");
        }

        if (input.SuspendAfterDays is < 0 or > MaxSuspendAfterDays)
        {
            return Error.Validation(ErrorCodes.InvalidBillingPolicy, "Plazo de suspensión inválido", $"Los días de gracia antes de suspender son de 0 a {MaxSuspendAfterDays}, o vacío para no suspender.");
        }

        if (input.ReminderDays is < 0 or > MaxReminderDays)
        {
            return Error.Validation(ErrorCodes.InvalidBillingPolicy, "Anticipación de avisos inválida", $"Los días de anticipación de los avisos son de 0 a {MaxReminderDays}; 0 no envía avisos anticipados.");
        }

        if (NoteTooLong(input.Note))
        {
            return Error.Validation(ErrorCodes.InvalidBillingPolicy, "Nota demasiado larga", $"La nota tiene hasta {MaxNoteLength} caracteres.");
        }

        var last = await db.Policies.AsNoTracking().OrderByDescending(p => p.Version).FirstOrDefaultAsync(cancellationToken);
        if (last is not null && input.EffectiveFrom <= last.EffectiveFrom)
        {
            return Error.Validation(ErrorCodes.InvalidBillingPolicy, "Fecha de vigencia inválida", $"La nueva política debe regir después de la anterior, que rige desde {last.EffectiveFrom:yyyy-MM-dd}.");
        }

        var policy = BillingPolicy.Create((last?.Version ?? 0) + 1, input.EffectiveFrom, input.DueDays, input.SuspendAfterDays, input.ReminderDays ?? DefaultReminderDays, Clean(input.Note), clock.GetUtcNow());
        db.Policies.Add(policy);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.BillingPolicyPublished, "billing_policy", policy.Id.ToString("D"), null,
                NewValues: new Dictionary<string, object?>
                {
                    ["version"] = policy.Version,
                    ["effectiveFrom"] = policy.EffectiveFrom.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    ["dueDays"] = policy.DueDays,
                    ["suspendAfterDays"] = policy.SuspendAfterDays,
                    ["reminderDays"] = policy.ReminderDays,
                }),
            cancellationToken);
        return ToDto(policy);
    }

    public async Task<Result<TenantTermsDto>> TermsOfAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        // The tenant registry and the plan answer for the scope: a tenant gets its own, platform staff any, and anyone else the same "not found".
        var tenant = await tenants.GetAsync(tenantId, cancellationToken);
        if (!tenant.IsSuccess)
        {
            return tenant.Error;
        }

        var plan = await limits.OfTenantAsync(tenantId, cancellationToken);
        if (plan is null)
        {
            return PlanMissing;
        }

        var assignedAt = tenant.Value.PlanAssignedAt ?? tenant.Value.CreatedAt;
        var versions = await db.Prices.AsNoTracking().Where(p => p.PlanId == plan.Id).OrderBy(p => p.EffectiveFrom).ToListAsync(cancellationToken);
        var terms = TermsResolver.Resolve(versions, LimaCalendar.Today(assignedAt));
        return new TenantTermsDto(tenantId.Value, plan.Id, plan.Code, plan.Name, plan.AllowsOverage, assignedAt, terms is null ? null : ToDto(terms.Price), terms?.FirstChargePeriod);
    }

    internal static PlanPriceDto ToDto(PlanPrice p) => new(p.Id, p.PlanId, p.Version, p.EffectiveFrom, p.MonthlyFee, p.IncludedDocuments, p.OverageUnitPrice, p.Note, p.CreatedAt);

    internal static BillingPolicyDto ToDto(BillingPolicy p) => new(p.Id, p.Version, p.EffectiveFrom, p.DueDays, p.SuspendAfterDays, p.ReminderDays, p.Note, p.CreatedAt);

    private static Error? Validate(PlanDto plan, PlanPriceInput input, DateOnly today)
    {
        if (input.EffectiveFrom.Day != 1)
        {
            return Error.Validation(ErrorCodes.InvalidPrice, "Fecha de vigencia inválida", "Un precio rige desde el primer día de un mes: así ningún mes se cobra con dos precios.");
        }

        if (input.EffectiveFrom <= today)
        {
            return Error.Validation(ErrorCodes.InvalidPrice, "Fecha de vigencia inválida", "El precio rige desde un mes futuro: no se publica con efecto sobre lo que ya pasó.");
        }

        if (input.MonthlyFee is < 0 or > MaxFee || decimal.Round(input.MonthlyFee, 2) != input.MonthlyFee)
        {
            return Error.Validation(ErrorCodes.InvalidPrice, "Cuota inválida", $"La cuota mensual va de 0 a {MaxFee:0} soles con hasta dos decimales.");
        }

        if (plan.AllowsOverage)
        {
            if (input.IncludedDocuments is null or < 0 or > MaxIncluded)
            {
                return Error.Validation(ErrorCodes.InvalidPrice, "Comprobantes incluidos inválidos", "Un plan que cobra el excedente indica cuántos comprobantes del mes incluye la cuota (0 o más).");
            }

            if (input.OverageUnitPrice is null or <= 0 or > MaxUnitPrice || decimal.Round(input.OverageUnitPrice.Value, 4) != input.OverageUnitPrice.Value)
            {
                return Error.Validation(ErrorCodes.InvalidPrice, "Precio de excedente inválido", $"El precio de cada comprobante excedente es mayor que 0 y hasta {MaxUnitPrice:0} soles con hasta cuatro decimales.");
            }
        }
        else if (input.IncludedDocuments is not null || input.OverageUnitPrice is not null)
        {
            return Error.Validation(ErrorCodes.InvalidPrice, "Excedente no admitido", "Este plan no cobra el excedente: sus comprobantes los limita el plan. No se indican comprobantes incluidos ni precio de excedente.");
        }

        return NoteTooLong(input.Note)
            ? Error.Validation(ErrorCodes.InvalidPrice, "Nota demasiado larga", $"La nota tiene hasta {MaxNoteLength} caracteres.")
            : null;
    }

    private static bool NoteTooLong(string? note) => note?.Trim().Length > MaxNoteLength;

    private static string? Clean(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    private Error? RequirePlatform() =>
        scope.Kind == DataScopeKind.Platform ? null : Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Solo la plataforma administra los precios.");
}
