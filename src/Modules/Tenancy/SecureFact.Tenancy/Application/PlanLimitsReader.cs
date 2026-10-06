using Microsoft.EntityFrameworkCore;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;
using SecureFact.Tenancy.Contracts;
using SecureFact.Tenancy.Infrastructure;

namespace SecureFact.Tenancy.Application;

/// <summary>Nothing is cached: a plan change applies to the very next request, and the read is one indexed lookup.</summary>
internal sealed class PlanLimitsReader(TenancyDbContext db) : IPlanLimits
{
    public async Task<PlanDto?> OfTenantAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        var plan = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId.Value)
            .Join(db.Plans.AsNoTracking(), t => t.PlanId, p => p.Id, (_, p) => p)
            .SingleOrDefaultAsync(cancellationToken);
        return plan is null ? null : PlanAdministration.ToDto(plan);
    }

    public async Task<int?> LimitAsync(TenantId tenantId, PlanResource resource, CancellationToken cancellationToken) =>
        Pick(await OfTenantAsync(tenantId, cancellationToken), resource);

    public async Task<Result<Unit>> EnsureCanAddAsync(TenantId tenantId, PlanResource resource, int current, CancellationToken cancellationToken)
    {
        var plan = await OfTenantAsync(tenantId, cancellationToken);
        return Pick(plan, resource) is { } limit && current >= limit
            ? IPlanLimits.LimitReached(resource, plan!.Name, limit)
            : Unit.Value;
    }

    private static int? Pick(PlanDto? plan, PlanResource resource) => resource switch
    {
        PlanResource.Companies => plan?.MaxCompanies,
        PlanResource.Users => plan?.MaxUsers,
        _ => plan?.MaxDocumentsPerMonth,
    };
}
