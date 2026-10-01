using Microsoft.EntityFrameworkCore;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;
using SecureFact.Tenancy.Domain;

namespace SecureFact.Tenancy.Infrastructure;

internal sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options, IDataScope scope)
    : TenantDbContext(options, scope)
{
    public const string Schema = "tenancy";

    public DbSet<Domain.Tenant> Tenants => Set<Domain.Tenant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Domain.Tenant>(builder =>
        {
            builder.ToTable("tenant");
            builder.HasKey(t => t.Id);
            builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(t => t.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
            builder.Property(t => t.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
            builder.Property(t => t.Environment).HasColumnName("environment").HasConversion<string>().HasMaxLength(20).IsRequired();
            builder.Property(t => t.ResellerId).HasColumnName("reseller_id");
            builder.Property(t => t.CreatedAt).HasColumnName("created_at").IsRequired();
            builder.Property(t => t.Version).IsRowVersion();

            // The tenant registry is keyed by id: a tenant sees only itself; platform scope sees all.
            builder.HasQueryFilter(t => IsPlatformScope || t.Id == CurrentTenantGuid);
        });
    }
}
