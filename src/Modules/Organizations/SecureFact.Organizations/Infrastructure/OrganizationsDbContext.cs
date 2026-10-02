using Microsoft.EntityFrameworkCore;
using SecureFact.Organizations.Domain;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Organizations.Infrastructure;

internal sealed class OrganizationsDbContext(DbContextOptions<OrganizationsDbContext> options, IDataScope scope)
    : TenantDbContext(options, scope)
{
    public const string Schema = "org";

    public DbSet<Company> Companies => Set<Company>();

    public DbSet<Establishment> Establishments => Set<Establishment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Company>(b =>
        {
            b.ToTable("company");
            b.HasKey(c => c.Id);
            b.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(c => c.Ruc).HasColumnName("ruc").HasMaxLength(11).IsFixedLength().IsRequired();
            b.Property(c => c.LegalName).HasColumnName("legal_name").HasMaxLength(250).IsRequired();
            b.Property(c => c.TradeName).HasColumnName("trade_name").HasMaxLength(250);
            b.Property(c => c.FiscalAddress).HasColumnName("fiscal_address").HasMaxLength(250).IsRequired();
            b.Property(c => c.Ubigeo).HasColumnName("ubigeo").HasMaxLength(6).IsFixedLength().IsRequired();
            b.Property(c => c.TaxRegime).HasColumnName("tax_regime").HasMaxLength(60);
            b.Property(c => c.ContactEmail).HasColumnName("contact_email").HasMaxLength(254);
            b.Property(c => c.TimeZone).HasColumnName("time_zone").HasMaxLength(60).IsRequired();
            b.Property(c => c.DefaultCurrency).HasColumnName("default_currency").HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(c => c.DetractionAccount).HasColumnName("detraction_account").HasMaxLength(100);
            b.Property(c => c.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
            b.Property(c => c.CreatedAt).HasColumnName("created_at");
            b.Property(c => c.UpdatedAt).HasColumnName("updated_at");
            b.Property(c => c.Version).IsRowVersion();
            b.HasIndex(c => new { c.TenantId, c.Ruc }).IsUnique();
            b.HasMany(c => c.Establishments).WithOne().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(c => c.Establishments).UsePropertyAccessMode(PropertyAccessMode.Field);
            ConfigureTenantOwned(b);
        });

        modelBuilder.Entity<Establishment>(b =>
        {
            b.ToTable("establishment");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(e => e.CompanyId).HasColumnName("company_id");
            b.Property(e => e.Code).HasColumnName("code").HasMaxLength(4).IsRequired();
            b.Property(e => e.Name).HasColumnName("name").HasMaxLength(250).IsRequired();
            b.Property(e => e.Address).HasColumnName("address").HasMaxLength(250).IsRequired();
            b.Property(e => e.Ubigeo).HasColumnName("ubigeo").HasMaxLength(6).IsFixedLength().IsRequired();
            b.Property(e => e.IsActive).HasColumnName("is_active");
            b.Property(e => e.CreatedAt).HasColumnName("created_at");
            b.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            b.Property(e => e.Version).IsRowVersion();
            b.HasIndex(e => new { e.CompanyId, e.Code }).IsUnique();
            ConfigureTenantOwned(b);
        });
    }
}
