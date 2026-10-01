using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Platform.Persistence;

/// <summary>Marker for entities that belong to exactly one tenant. The column is named <c>tenant_id</c>.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}

public sealed class TenantMismatchException(string message) : InvalidOperationException(message);

/// <summary>
/// Base class for module DbContexts. Applies the application-level tenant filter (first defence layer, ADR-003),
/// stamps the owner on new rows and refuses writes that would cross tenants. PostgreSQL RLS is the second layer.
/// </summary>
public abstract class TenantDbContext(DbContextOptions options, IDataScope scope) : DbContext(options)
{
    protected Guid? CurrentTenantGuid => scope.Current?.Value;

    protected bool IsPlatformScope => scope.Kind == DataScopeKind.Platform;

    /// <summary>Call from <c>OnModelCreating</c> for every <see cref="ITenantOwned"/> entity.</summary>
    protected void ConfigureTenantOwned<TEntity>(EntityTypeBuilder<TEntity> builder)
        where TEntity : class, ITenantOwned
    {
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.HasIndex(e => e.TenantId);
        builder.HasQueryFilter(e => IsPlatformScope || e.TenantId == CurrentTenantGuid);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenantOwnership();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTenantOwnership();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnforceTenantOwnership()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            var property = entry.Property(nameof(ITenantOwned.TenantId));
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.TenantId == Guid.Empty)
                    {
                        property.CurrentValue = CurrentTenantGuid
                            ?? throw new TenantMismatchException("Cannot create a tenant-owned row without a tenant scope.");
                    }
                    else if (!IsPlatformScope && entry.Entity.TenantId != CurrentTenantGuid)
                    {
                        throw new TenantMismatchException("Cannot create a row owned by a different tenant.");
                    }

                    break;
                case EntityState.Modified when property.IsModified:
                    throw new TenantMismatchException("The owner tenant of a row cannot be changed.");
                case EntityState.Modified when !IsPlatformScope && entry.Entity.TenantId != CurrentTenantGuid:
                    throw new TenantMismatchException("Cannot modify a row owned by a different tenant.");
                case EntityState.Deleted when !IsPlatformScope && entry.Entity.TenantId != CurrentTenantGuid:
                    throw new TenantMismatchException("Cannot delete a row owned by a different tenant.");
            }
        }
    }
}
