using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Subscriptions.Contracts;
using SecureFact.Subscriptions.Domain;
using SecureFact.Subscriptions.Infrastructure;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Subscriptions.Application;

internal sealed class Commissions(SubscriptionsDbContext db, IDataScope scope, ICurrentUser actor, TimeProvider clock, IAuditTrail audit, IResellerAdministration resellers) : ICommissions
{
    private const int MaxTiers = 10;
    private const int MaxMinAccounts = 100_000;
    private const int MaxTextLength = 300;
    private const int MaxReferenceLength = 100;

    /// <summary>The commission of a payment, added to the same transaction that records it (<see cref="Collections"/>). A reversal adds the opposite entry of the one the payment had.</summary>
    internal async Task AccrueAsync(Charge charge, Payment payment, Guid? reversesPaymentId, CancellationToken cancellationToken)
    {
        var month = LimaCalendar.MonthStart(LimaCalendar.Today(payment.RecordedAt));
        if (reversesPaymentId is { } original)
        {
            var earned = await db.Entries.AsNoTracking().SingleOrDefaultAsync(e => e.PaymentId == original, cancellationToken);
            if (earned is not null)
            {
                db.Entries.Add(CommissionEntry.Create(
                    earned.ResellerId, earned.TenantId, earned.TenantName, earned.ChargeId, earned.ChargePeriod, payment.Id, month, -earned.BaseAmount, earned.Rate, -earned.Amount, earned.ScheduleId,
                    payment.RecordedAt));
                await db.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        if (charge.ResellerId is not { } resellerId || charge.CommissionScheduleId is not { } scheduleId || charge.TotalAmount <= 0m)
        {
            return;
        }

        var rate = await RateAsync(scheduleId, await resellers.CountActiveTenantsAsync(resellerId, cancellationToken), cancellationToken);
        var baseAmount = Round(payment.Amount * charge.NetAmount / charge.TotalAmount);
        db.Entries.Add(CommissionEntry.Create(
            resellerId, charge.TenantId, charge.TenantName, charge.Id, charge.Period, payment.Id, month, baseAmount, rate, Round(baseAmount * rate), scheduleId, payment.RecordedAt));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The schedule in force on a day, or the first one when none was yet (an account that came before the first schedule gets it). Null while there are no schedules.</summary>
    internal async Task<Guid?> ScheduleForAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var versions = await db.Schedules.AsNoTracking().OrderBy(s => s.EffectiveFrom).Select(s => new { s.Id, s.EffectiveFrom }).ToListAsync(cancellationToken);
        return versions.Count == 0 ? null : (versions.LastOrDefault(v => v.EffectiveFrom <= day) ?? versions[0]).Id;
    }

    public async Task<Result<IReadOnlyList<CommissionScheduleDto>>> ListSchedulesAsync(CancellationToken cancellationToken)
    {
        if (RequirePlatformStaff() is { } denied)
        {
            return denied;
        }

        return (await LoadSchedulesAsync(null, cancellationToken)).ToList();
    }

    public async Task<Result<CommissionScheduleDto>> PublishScheduleAsync(CommissionScheduleInput input, CancellationToken cancellationToken)
    {
        if (RequirePlatformStaff() is { } denied)
        {
            return denied;
        }

        var today = LimaCalendar.Today(clock.GetUtcNow());
        if (Validate(input, today) is { } invalid)
        {
            return invalid;
        }

        var last = await db.Schedules.AsNoTracking().OrderByDescending(s => s.Version).FirstOrDefaultAsync(cancellationToken);
        if (last is not null && input.EffectiveFrom <= last.EffectiveFrom)
        {
            return Error.Validation(ErrorCodes.InvalidCommission, "Fecha de vigencia inválida", $"Los nuevos términos deben regir después de los anteriores, que rigen desde {last.EffectiveFrom:yyyy-MM-dd}.");
        }

        var schedule = CommissionSchedule.Create((last?.Version ?? 0) + 1, input.EffectiveFrom, string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(), clock.GetUtcNow());
        db.Schedules.Add(schedule);
        db.Tiers.AddRange(input.Tiers.Select(t => CommissionTier.Create(schedule.Id, t.MinAccounts, t.Rate)));
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.CommissionScheduleCreated, "commission_schedule", schedule.Id.ToString("D"), null,
                NewValues: new Dictionary<string, object?>
                {
                    ["version"] = schedule.Version,
                    ["effectiveFrom"] = schedule.EffectiveFrom.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    ["tiers"] = input.Tiers.Select(t => $"{t.MinAccounts}:{t.Rate}").ToList(),
                }),
            cancellationToken);
        return (await LoadSchedulesAsync(schedule.Id, cancellationToken)).Single();
    }

    public async Task<Result<CommissionOverviewDto>> OverviewAsync(Guid resellerId, CancellationToken cancellationToken)
    {
        if (Authorize(resellerId) is { } denied)
        {
            return denied;
        }

        var accounts = await resellers.CountActiveTenantsAsync(resellerId, cancellationToken);
        var today = LimaCalendar.Today(clock.GetUtcNow());
        var current = await ScheduleForAsync(today, cancellationToken);
        var schedule = current is { } id ? (await LoadSchedulesAsync(id, cancellationToken)).Single() : null;
        decimal? rate = schedule?.Tiers.Where(t => t.MinAccounts <= accounts).OrderByDescending(t => t.MinAccounts).Select(t => (decimal?)t.Rate).FirstOrDefault();
        return new CommissionOverviewDto(resellerId, accounts, schedule, rate, await MonthsAsync(resellerId, cancellationToken));
    }

    public async Task<Result<CommissionStatementDto>> StatementAsync(Guid resellerId, DateOnly month, CancellationToken cancellationToken)
    {
        if (Authorize(resellerId) is { } denied)
        {
            return denied;
        }

        var first = LimaCalendar.MonthStart(month);
        var items = await db.Entries.AsNoTracking().Where(e => e.ResellerId == resellerId && e.Month == first).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(cancellationToken);
        var settlement = await db.Settlements.AsNoTracking().SingleOrDefaultAsync(s => s.ResellerId == resellerId && s.Month == first, cancellationToken);
        return new CommissionStatementDto(
            resellerId,
            new CommissionMonthDto(first, items.Sum(e => e.Amount), items.Count, settlement is null ? null : ToDto(settlement)),
            items.Select(e => new CommissionEntryDto(e.Id, e.TenantId, e.TenantName, e.ChargePeriod, e.PaymentId, e.Month, e.BaseAmount, e.Rate, e.Amount, e.CreatedAt)).ToList());
    }

    public async Task<Result<CommissionSettlementDto>> SettleAsync(Guid resellerId, DateOnly month, SettleCommissionRequest request, CancellationToken cancellationToken)
    {
        if (RequirePlatformStaff() is { } denied)
        {
            return denied;
        }

        var now = clock.GetUtcNow();
        var today = LimaCalendar.Today(now);
        var first = LimaCalendar.MonthStart(month);
        if (first >= LimaCalendar.MonthStart(today))
        {
            return Error.Validation(ErrorCodes.SettlementNotAllowed, "Mes abierto", "Solo se liquida un mes ya cerrado: mientras corre, sus comisiones pueden cambiar.");
        }

        if (request.SettledOn > today || request.SettledOn < first.AddMonths(1))
        {
            return Error.Validation(ErrorCodes.SettlementNotAllowed, "Fecha de liquidación inválida", "La liquidación es de un día posterior al cierre del mes y no futuro.");
        }

        if (request.Reference?.Trim().Length > MaxReferenceLength || request.Note?.Trim().Length > MaxTextLength)
        {
            return Error.Validation(ErrorCodes.SettlementNotAllowed, "Texto demasiado largo", $"La referencia tiene hasta {MaxReferenceLength} caracteres y la nota hasta {MaxTextLength}.");
        }

        var entries = await db.Entries.AsNoTracking().Where(e => e.ResellerId == resellerId && e.Month == first).ToListAsync(cancellationToken);
        if (entries.Count == 0)
        {
            return Error.Validation(ErrorCodes.SettlementNotAllowed, "Sin comisiones", "El revendedor no tiene comisiones en ese mes.");
        }

        if (await db.Settlements.AnyAsync(s => s.ResellerId == resellerId && s.Month == first, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.SettlementNotAllowed, "Mes ya liquidado", "Ese mes ya fue liquidado con el revendedor.");
        }

        var settlement = CommissionSettlement.Create(
            resellerId, first, entries.Sum(e => e.Amount), entries.Count, request.SettledOn, string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim(),
            string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(), now);
        db.Settlements.Add(settlement);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.CommissionSettled, "commission_settlement", settlement.Id.ToString("D"), null,
                NewValues: new Dictionary<string, object?>
                {
                    ["reseller"] = resellerId,
                    ["month"] = first.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
                    ["total"] = settlement.Total,
                    ["entries"] = settlement.Entries,
                    ["reference"] = settlement.Reference,
                }),
            cancellationToken);
        return ToDto(settlement);
    }

    private async Task<IReadOnlyList<CommissionMonthDto>> MonthsAsync(Guid resellerId, CancellationToken cancellationToken)
    {
        var totals = await db.Entries.AsNoTracking().Where(e => e.ResellerId == resellerId)
            .GroupBy(e => e.Month).Select(g => new { Month = g.Key, Total = g.Sum(e => e.Amount), Count = g.Count() }).OrderByDescending(g => g.Month).ToListAsync(cancellationToken);
        var settlements = await db.Settlements.AsNoTracking().Where(s => s.ResellerId == resellerId).ToListAsync(cancellationToken);
        return totals.Select(t => new CommissionMonthDto(t.Month, t.Total, t.Count, settlements.Where(s => s.Month == t.Month).Select(ToDto).FirstOrDefault())).ToList();
    }

    private async Task<decimal> RateAsync(Guid scheduleId, int accounts, CancellationToken cancellationToken)
    {
        var tiers = await db.Tiers.AsNoTracking().Where(t => t.ScheduleId == scheduleId && t.MinAccounts <= accounts).OrderByDescending(t => t.MinAccounts).Take(1).ToListAsync(cancellationToken);
        return tiers.Count == 0 ? 0m : tiers[0].Rate;
    }

    private async Task<IReadOnlyList<CommissionScheduleDto>> LoadSchedulesAsync(Guid? only, CancellationToken cancellationToken)
    {
        var schedules = await db.Schedules.AsNoTracking().Where(s => only == null || s.Id == only).OrderBy(s => s.Version).ToListAsync(cancellationToken);
        var ids = schedules.Select(s => s.Id).ToList();
        var tiers = await db.Tiers.AsNoTracking().Where(t => ids.Contains(t.ScheduleId)).ToListAsync(cancellationToken);
        return schedules.Select(s => new CommissionScheduleDto(
            s.Id, s.Version, s.EffectiveFrom, tiers.Where(t => t.ScheduleId == s.Id).OrderBy(t => t.MinAccounts).Select(t => new CommissionTierInput(t.MinAccounts, t.Rate)).ToList(), s.Note, s.CreatedAt)).ToList();
    }

    private static Error? Validate(CommissionScheduleInput input, DateOnly today)
    {
        if (input.EffectiveFrom <= today)
        {
            return Error.Validation(ErrorCodes.InvalidCommission, "Fecha de vigencia inválida", "Los términos rigen desde un día futuro: no se publican con efecto sobre lo que ya pasó.");
        }

        var tiers = input.Tiers ?? [];
        if (tiers.Count is < 1 or > MaxTiers)
        {
            return Error.Validation(ErrorCodes.InvalidCommission, "Tramos inválidos", $"Los términos tienen de 1 a {MaxTiers} tramos.");
        }

        if (tiers[0].MinAccounts != 0)
        {
            return Error.Validation(ErrorCodes.InvalidCommission, "Tramos inválidos", "El primer tramo empieza en 0 cuentas: todo revendedor tiene una comisión.");
        }

        for (var i = 0; i < tiers.Count; i++)
        {
            var tier = tiers[i];
            if (tier.Rate is < 0m or > 1m || decimal.Round(tier.Rate, 4) != tier.Rate)
            {
                return Error.Validation(ErrorCodes.InvalidCommission, "Porcentaje inválido", "Cada porcentaje va de 0 a 1 (100 %) con hasta cuatro decimales.");
            }

            if (tier.MinAccounts is < 0 or > MaxMinAccounts || (i > 0 && tier.MinAccounts <= tiers[i - 1].MinAccounts))
            {
                return Error.Validation(ErrorCodes.InvalidCommission, "Tramos inválidos", "Los tramos van en orden, cada uno desde más cuentas que el anterior.");
            }
        }

        return input.Note?.Trim().Length > MaxTextLength
            ? Error.Validation(ErrorCodes.InvalidCommission, "Nota demasiado larga", $"La nota tiene hasta {MaxTextLength} caracteres.")
            : null;
    }

    private static CommissionSettlementDto ToDto(CommissionSettlement s) => new(s.Id, s.ResellerId, s.Month, s.Total, s.Entries, s.SettledOn, s.Reference, s.Note, s.CreatedAt);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private Error? RequirePlatformStaff() =>
        scope.Kind == DataScopeKind.Platform && actor.IsPlatform
            ? null
            : Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Solo el personal de la plataforma administra las comisiones.");

    /// <summary>Platform staff read any reseller; a reseller reads only itself, taken from its token (ADR-043).</summary>
    private Error? Authorize(Guid resellerId) =>
        scope.Kind == DataScopeKind.Platform && (actor.IsPlatform || actor.ResellerId == resellerId)
            ? null
            : Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Solo el personal de la plataforma o el propio revendedor leen sus comisiones.");
}
