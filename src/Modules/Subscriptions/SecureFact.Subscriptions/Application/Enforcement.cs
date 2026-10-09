using Microsoft.EntityFrameworkCore;
using SecureFact.SharedKernel.Domain;
using SecureFact.Subscriptions.Infrastructure;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Subscriptions.Application;

/// <summary>Suspends the accounts that did not pay and lifts the suspension when they do (ADR-064). It only touches the suspensions it made: one of the platform or of a reseller is never lifted by a payment.</summary>
internal sealed class Enforcement(SubscriptionsDbContext db, ITenantAdministration tenants)
{
    private IQueryable<Domain.Charge> PastGrace(DateOnly today) =>
        db.Charges.AsNoTracking().Where(c => c.VoidedAt == null && c.SuspendOn != null && c.SuspendOn <= today && (db.Payments.Where(p => p.ChargeId == c.Id).Sum(p => (decimal?)p.Amount) ?? 0m) < c.TotalAmount);

    /// <summary>The tenants with a charge unpaid past its grace.</summary>
    public async Task<IReadOnlySet<Guid>> TenantsPastGraceAsync(DateOnly today, CancellationToken cancellationToken) =>
        (await PastGrace(today).Select(c => c.TenantId).Distinct().ToListAsync(cancellationToken)).ToHashSet();

    public Task<bool> HasPastGraceAsync(Guid tenantId, DateOnly today, CancellationToken cancellationToken) =>
        PastGrace(today).AnyAsync(c => c.TenantId == tenantId, cancellationToken);

    /// <summary>Lifts the suspension for non-payment of the tenant when nothing is unpaid past its grace any more. Returns whether it was lifted.</summary>
    public async Task<bool> ReactivateIfClearedAsync(TenantId tenantId, DateOnly today, CancellationToken cancellationToken)
    {
        var tenant = await tenants.GetAsync(tenantId, cancellationToken);
        if (!tenant.IsSuccess || tenant.Value is not { Status: TenantStatus.Suspended, SuspendedBy: SuspensionSource.NonPayment })
        {
            return false;
        }

        if (await HasPastGraceAsync(tenantId.Value, today, cancellationToken))
        {
            return false;
        }

        var lifted = await tenants.ChangeStatusAsync(tenantId, TenantStatus.Active, "Sin cargos vencidos: la cuenta está al día", SuspensionSource.NonPayment, cancellationToken);
        return lifted.IsSuccess;
    }
}
