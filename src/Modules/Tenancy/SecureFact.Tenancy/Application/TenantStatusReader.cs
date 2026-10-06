using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SecureFact.SharedKernel.Domain;
using SecureFact.Tenancy.Contracts;
using SecureFact.Tenancy.Infrastructure;

namespace SecureFact.Tenancy.Application;

internal sealed class TenantStatusReader(TenancyDbContext db, IMemoryCache cache) : ITenantStatusReader
{
    public static string Key(Guid tenantId) => $"tenant-status:{tenantId:N}";

    public async Task<TenantStatus?> GetStatusAsync(TenantId id, bool fresh, CancellationToken cancellationToken)
    {
        var key = Key(id.Value);
        if (!fresh && cache.TryGetValue(key, out TenantStatus? cached))
        {
            return cached;
        }

        var status = await db.Tenants.AsNoTracking().Where(t => t.Id == id.Value).Select(t => (TenantStatus?)t.Status).SingleOrDefaultAsync(cancellationToken);
        cache.Set(key, status, ITenantStatusReader.CacheLifetime);
        return status;
    }
}
