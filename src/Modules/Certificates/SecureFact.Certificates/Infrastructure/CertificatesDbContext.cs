using Microsoft.EntityFrameworkCore;
using SecureFact.Certificates.Domain;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Certificates.Infrastructure;

internal sealed class CertificatesDbContext(DbContextOptions<CertificatesDbContext> options, IDataScope scope) : TenantDbContext(options, scope)
{
    public const string Schema = "certificates";

    public DbSet<CompanyCertificate> Certificates => Set<CompanyCertificate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<CompanyCertificate>(b =>
        {
            b.ToTable("company_certificate");
            b.HasKey(c => c.Id);
            b.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(c => c.CompanyId).HasColumnName("company_id");
            b.Property(c => c.Subject).HasColumnName("subject").HasMaxLength(500).IsRequired();
            b.Property(c => c.Thumbprint).HasColumnName("thumbprint").HasMaxLength(64).IsRequired();
            b.Property(c => c.SerialNumber).HasColumnName("serial_number").HasMaxLength(128).IsRequired();
            b.Property(c => c.NotBefore).HasColumnName("not_before");
            b.Property(c => c.NotAfter).HasColumnName("not_after");
            b.Property(c => c.ProtectedPfx).HasColumnName("protected_pfx").IsRequired();
            b.Property(c => c.IsActive).HasColumnName("is_active");
            b.Property(c => c.RucInSubject).HasColumnName("ruc_in_subject");
            b.Property(c => c.UploadedBy).HasColumnName("uploaded_by");
            b.Property(c => c.CreatedAt).HasColumnName("created_at");
            b.Property(c => c.DeactivatedAt).HasColumnName("deactivated_at");
            b.Property(c => c.Version).IsRowVersion();
            b.HasIndex(c => new { c.TenantId, c.CompanyId, c.Thumbprint }).IsUnique();

            // At most one active certificate per company: a race between two uploads cannot leave two signers.
            b.HasIndex(c => new { c.TenantId, c.CompanyId }).IsUnique().HasFilter("is_active").HasDatabaseName("ux_company_certificate_active");
            ConfigureTenantOwned(b);
        });
    }
}
