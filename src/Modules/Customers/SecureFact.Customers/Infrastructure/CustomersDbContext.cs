using Microsoft.EntityFrameworkCore;
using SecureFact.Customers.Domain;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Customers.Infrastructure;

internal sealed class CustomersDbContext(DbContextOptions<CustomersDbContext> options, IDataScope scope) : TenantDbContext(options, scope)
{
    public const string Schema = "customers";

    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Customer>(b =>
        {
            b.ToTable("customer");
            b.HasKey(c => c.Id);
            b.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(c => c.DocumentTypeCode).HasColumnName("document_type_code").HasMaxLength(2).IsRequired();
            b.Property(c => c.DocumentNumber).HasColumnName("document_number").HasMaxLength(20).IsRequired();
            b.Property(c => c.Name).HasColumnName("name").HasMaxLength(250).IsRequired();
            b.Property(c => c.Address).HasColumnName("address").HasMaxLength(250);
            b.Property(c => c.Email).HasColumnName("email").HasMaxLength(254);
            b.Property(c => c.Phone).HasColumnName("phone").HasMaxLength(30);
            b.Property(c => c.IsActive).HasColumnName("is_active");
            b.Property(c => c.CreatedAt).HasColumnName("created_at");
            b.Property(c => c.UpdatedAt).HasColumnName("updated_at");
            b.Property(c => c.Version).IsRowVersion();
            b.HasIndex(c => new { c.TenantId, c.DocumentTypeCode, c.DocumentNumber }).IsUnique();
            b.HasIndex(c => new { c.TenantId, c.Name });
            ConfigureTenantOwned(b);
        });
    }
}
