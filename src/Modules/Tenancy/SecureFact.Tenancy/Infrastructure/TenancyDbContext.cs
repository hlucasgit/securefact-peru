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

    public DbSet<Domain.Plan> Plans => Set<Domain.Plan>();

    public DbSet<Domain.Reseller> Resellers => Set<Domain.Reseller>();

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
            builder.Property(t => t.PlanId).HasColumnName("plan_id").IsRequired();
            builder.Property(t => t.CreatedAt).HasColumnName("created_at").IsRequired();
            builder.Property(t => t.Version).IsRowVersion();

            // The tenant registry is keyed by id: a tenant sees only itself; platform scope sees all.
            builder.HasQueryFilter(t => IsPlatformScope || t.Id == CurrentTenantGuid);
        });

        // Plans are platform reference data: every scope reads them (a tenant sees its own limits), only the platform scope writes (RLS global_read / platform_write).
        modelBuilder.Entity<Domain.Plan>(builder =>
        {
            builder.ToTable("plan");
            builder.HasKey(p => p.Id);
            builder.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(p => p.Code).HasColumnName("code").HasMaxLength(40).IsRequired();
            builder.Property(p => p.Name).HasColumnName("name").HasMaxLength(80).IsRequired();
            builder.Property(p => p.MaxCompanies).HasColumnName("max_companies");
            builder.Property(p => p.MaxUsers).HasColumnName("max_users");
            builder.Property(p => p.MaxDocumentsPerMonth).HasColumnName("max_documents_per_month");
            builder.Property(p => p.ResellerId).HasColumnName("reseller_id");
            builder.Property(p => p.IsActive).HasColumnName("is_active").IsRequired();
            builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
            builder.Property(p => p.Version).IsRowVersion();
            builder.HasIndex(p => p.Code).IsUnique();
        });

        // Resellers belong to the platform: only the platform scope reads or writes them (RLS platform_only). A reseller user acts in platform scope and the services filter by its reseller.
        modelBuilder.Entity<Domain.Reseller>(builder =>
        {
            builder.ToTable("reseller");
            builder.HasKey(r => r.Id);
            builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(r => r.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
            builder.Property(r => r.IsActive).HasColumnName("is_active").IsRequired();
            builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();
            builder.Property(r => r.Version).IsRowVersion();
        });
    }
}
