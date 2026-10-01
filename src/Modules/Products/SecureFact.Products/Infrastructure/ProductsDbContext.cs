using Microsoft.EntityFrameworkCore;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;
using SecureFact.Products.Domain;

namespace SecureFact.Products.Infrastructure;

internal sealed class ProductsDbContext(DbContextOptions<ProductsDbContext> options, IDataScope scope) : TenantDbContext(options, scope)
{
    public const string Schema = "products";

    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("product");
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(p => p.InternalCode).HasColumnName("internal_code").HasMaxLength(50).IsRequired();
            b.Property(p => p.Description).HasColumnName("description").HasMaxLength(500).IsRequired();
            b.Property(p => p.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(20).IsRequired();
            b.Property(p => p.UnitCode).HasColumnName("unit_code").HasMaxLength(3).IsRequired();
            b.Property(p => p.UnitValue).HasColumnName("unit_value").HasPrecision(22, 10);
            b.Property(p => p.IgvAffectationCode).HasColumnName("igv_affectation_code").HasMaxLength(2).IsRequired();
            b.Property(p => p.SunatProductCode).HasColumnName("sunat_product_code").HasMaxLength(8);
            b.Property(p => p.Category).HasColumnName("category").HasMaxLength(100);
            b.Property(p => p.IsActive).HasColumnName("is_active");
            b.Property(p => p.CreatedAt).HasColumnName("created_at");
            b.Property(p => p.UpdatedAt).HasColumnName("updated_at");
            b.Property(p => p.Version).IsRowVersion();
            b.HasIndex(p => new { p.TenantId, p.InternalCode }).IsUnique();
            b.HasIndex(p => new { p.TenantId, p.Description });
            ConfigureTenantOwned(b);
        });
    }
}
