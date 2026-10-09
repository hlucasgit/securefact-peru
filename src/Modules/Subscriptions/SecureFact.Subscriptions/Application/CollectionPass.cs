using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SecureFact.Billing.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.Rules.Contracts;
using SecureFact.SharedKernel.Domain;
using SecureFact.Subscriptions.Contracts;
using SecureFact.Subscriptions.Domain;
using SecureFact.Subscriptions.Infrastructure;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Subscriptions.Application;

/// <summary>Runs <see cref="CollectionPass"/> in platform scope, a fresh scope per pass, as the other background work does.</summary>
internal sealed class CollectionProcessor(IServiceScopeFactory scopes) : ICollectionProcessor
{
    public async Task<CollectionPassResult> RunAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UsePlatform("subscriptions: collection pass");
        return await scope.ServiceProvider.GetRequiredService<CollectionPass>().RunAsync(now, cancellationToken);
    }
}

/// <summary>
/// The monthly pass (ADR-062, ADR-064): a charge for each month that closed since the first month a tenant is charged, then the suspension of the accounts with a charge unpaid past its grace and
/// the reactivation of the ones that paid. It is idempotent: a month already charged is skipped, so running it twice, or from two processes, changes nothing.
/// </summary>
internal sealed partial class CollectionPass(
    SubscriptionsDbContext db,
    ITenantAdministration tenants,
    IPlanAdministration plans,
    IDocumentService documents,
    IRuleProvider rules,
    Commissions commissions,
    Enforcement enforcement,
    ILogger<CollectionPass> logger)
{
    private const int PageSize = 100;
    private const int MaxChargesPerTenantPerPass = 24;

    public async Task<CollectionPassResult> RunAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var today = LimaCalendar.Today(now);
        var catalogue = await plans.ListAsync(cancellationToken);
        if (!catalogue.IsSuccess)
        {
            LogNoCatalogue(logger, catalogue.Error.Code);
            return new CollectionPassResult(0, 0, 0);
        }

        var planById = catalogue.Value.ToDictionary(p => p.Id);
        var policies = await db.Policies.AsNoTracking().OrderBy(p => p.EffectiveFrom).ToListAsync(cancellationToken);
        var lastClosed = LimaCalendar.MonthStart(today).AddMonths(-1);

        var created = 0;
        var live = new List<TenantDto>();
        for (var skip = 0; ; skip += PageSize)
        {
            var page = await tenants.ListAsync(null, null, skip, PageSize, cancellationToken);
            if (!page.IsSuccess)
            {
                LogNoTenants(logger, page.Error.Code);
                break;
            }

            foreach (var tenant in page.Value.Where(t => t.Status != TenantStatus.Closed))
            {
                live.Add(tenant);
                try
                {
                    created += await ChargeAsync(tenant, planById, policies, lastClosed, today, now, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // One tenant that fails must not stop the others. Nothing of the tenant goes to the log, only its id.
                    LogChargeFailed(logger, exception, tenant.Id.Value);
                }
            }

            if (page.Value.Count < PageSize)
            {
                break;
            }
        }

        var (suspended, reactivated) = await EnforceAsync(live, today, cancellationToken);
        return new CollectionPassResult(created, suspended, reactivated);
    }

    private async Task<int> ChargeAsync(
        TenantDto tenant, Dictionary<Guid, PlanDto> planById, List<BillingPolicy> policies, DateOnly lastClosed, DateOnly today, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!planById.TryGetValue(tenant.PlanId, out var plan))
        {
            return 0;
        }

        var versions = await db.Prices.AsNoTracking().Where(p => p.PlanId == plan.Id).OrderBy(p => p.EffectiveFrom).ToListAsync(cancellationToken);
        var terms = TermsResolver.Resolve(versions, LimaCalendar.Today(tenant.PlanAssignedAt ?? tenant.CreatedAt));
        if (terms is null || terms.FirstChargePeriod > lastClosed || (terms.Price.MonthlyFee == 0m && terms.Price.OverageUnitPrice is null))
        {
            return 0;
        }

        var charged = (await db.Charges.AsNoTracking().Where(c => c.TenantId == tenant.Id.Value).Select(c => c.Period).ToListAsync(cancellationToken)).ToHashSet();
        var policy = policies.LastOrDefault(p => p.EffectiveFrom <= today) ?? (policies.Count > 0 ? policies[0] : null);
        var schedule = tenant.ResellerId is null ? null : await commissions.ScheduleForAsync(LimaCalendar.Today(tenant.ResellerAssignedAt ?? tenant.CreatedAt), cancellationToken);
        var igv = await rules.ResolveDecimalAsync(RuleCodes.IgvRate, "rate", today, cancellationToken);
        if (!igv.IsSuccess)
        {
            LogNoRate(logger, igv.Error.Code);
            return 0;
        }

        var made = 0;
        for (var period = terms.FirstChargePeriod; period <= lastClosed && made < MaxChargesPerTenantPerPass; period = period.AddMonths(1))
        {
            if (charged.Contains(period))
            {
                continue;
            }

            var issued = await documents.CountIssuedAsync(tenant.Id.Value, LimaCalendar.StartOf(period), LimaCalendar.StartOf(period.AddMonths(1)), cancellationToken);
            var charge = Build(tenant, plan, terms.Price, period, issued, igv.Value, policy, today, schedule, tenant.ResellerId, now);
            if (await SaveAsync(charge, cancellationToken))
            {
                made++;
            }
        }

        return made;
    }

    internal static Charge Build(
        TenantDto tenant, PlanDto plan, PlanPrice price, DateOnly period, int documentsIssued, decimal taxRate, BillingPolicy? policy, DateOnly today, Guid? scheduleId, Guid? resellerId, DateTimeOffset now)
    {
        var overageDocuments = price.IncludedDocuments is { } included && price.OverageUnitPrice is not null ? Math.Max(0, documentsIssued - included) : 0;
        var overageAmount = Round(overageDocuments * (price.OverageUnitPrice ?? 0m));
        var net = price.MonthlyFee + overageAmount;
        var tax = Round(net * taxRate);
        var dueDays = policy?.DueDays ?? 0;
        var dueOn = today.AddDays(dueDays);
        DateOnly? suspendOn = policy?.SuspendAfterDays is { } grace ? dueOn.AddDays(grace) : null;
        return Charge.Create(
            tenant.Id.Value, tenant.Name, period, plan.Id, plan.Code, plan.Name, price.Id, price.MonthlyFee, price.IncludedDocuments, documentsIssued, overageDocuments, price.OverageUnitPrice, overageAmount,
            net, taxRate, tax, net + tax, today, dueOn, suspendOn, resellerId, scheduleId, now);
    }

    /// <summary>Takes a turn per tenant and month, looks again and adds the charge: two passes at once leave one.</summary>
    private async Task<bool> SaveAsync(Charge charge, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var key = $"charge-create:{charge.TenantId:N}:{charge.Period:yyyyMM}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
        if (await db.Charges.AnyAsync(c => c.TenantId == charge.TenantId && c.Period == charge.Period, cancellationToken))
        {
            return false;
        }

        db.Charges.Add(charge);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<(int Suspended, int Reactivated)> EnforceAsync(IReadOnlyList<TenantDto> live, DateOnly today, CancellationToken cancellationToken)
    {
        var late = await enforcement.TenantsPastGraceAsync(today, cancellationToken);
        var suspended = 0;
        var reactivated = 0;
        foreach (var tenant in live)
        {
            try
            {
                if (late.Contains(tenant.Id.Value) && tenant.Status == TenantStatus.Active)
                {
                    var result = await tenants.ChangeStatusAsync(tenant.Id, TenantStatus.Suspended, "Cargo vencido sin pagar: suspensión por falta de pago", SuspensionSource.NonPayment, cancellationToken);
                    suspended += result.IsSuccess ? 1 : 0;
                }
                else if (!late.Contains(tenant.Id.Value) && tenant is { Status: TenantStatus.Suspended, SuspendedBy: SuspensionSource.NonPayment })
                {
                    reactivated += await enforcement.ReactivateIfClearedAsync(tenant.Id, today, cancellationToken) ? 1 : 0;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogEnforceFailed(logger, exception, tenant.Id.Value);
            }
        }

        return (suspended, reactivated);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    [LoggerMessage(Level = LogLevel.Error, Message = "Collection pass: the plan catalogue could not be read: {Code}.")]
    private static partial void LogNoCatalogue(ILogger logger, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Collection pass: the tenants could not be listed: {Code}.")]
    private static partial void LogNoTenants(ILogger logger, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Collection pass: the IGV rate could not be resolved: {Code}.")]
    private static partial void LogNoRate(ILogger logger, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Collection pass: charging tenant {TenantId} failed unexpectedly.")]
    private static partial void LogChargeFailed(ILogger logger, Exception exception, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Collection pass: enforcing the payment status of tenant {TenantId} failed unexpectedly.")]
    private static partial void LogEnforceFailed(ILogger logger, Exception exception, Guid tenantId);
}
