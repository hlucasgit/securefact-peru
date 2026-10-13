using Microsoft.EntityFrameworkCore;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Export;
using SecureFact.Tenancy.Infrastructure;

namespace SecureFact.Tenancy.Application;

/// <summary>The account itself: its name, state and plan. Not its reseller's terms or prices, which belong to the reseller.</summary>
internal sealed class TenancyAccountData(TenancyDbContext db, IDataScope scope) : IAccountDataContributor
{
    public async Task WriteAsync(IAccountDataWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var id = scope.Current?.Value ?? Guid.Empty;
        var rows = await db.Tenants.AsNoTracking().Where(t => t.Id == id)
            .Join(db.Plans.AsNoTracking(), t => t.PlanId, p => p.Id, (t, p) => new { t.Name, t.Status, t.Environment, PlanCode = p.Code, PlanName = p.Name, t.PlanAssignedAt, t.CreatedAt })
            .ToListAsync(cancellationToken);
        await writer.WriteTableAsync(
            "cuenta",
            ["nombre", "estado", "ambiente", "plan_codigo", "plan_nombre", "plan_desde", "creada"],
            rows.Select(r => (IReadOnlyList<string?>)[r.Name, r.Status.ToString(), r.Environment.ToString(), r.PlanCode, r.PlanName, AccountCsv.Stamp(r.PlanAssignedAt), AccountCsv.Stamp(r.CreatedAt)]).ToAsyncEnumerable(),
            cancellationToken);
    }
}
