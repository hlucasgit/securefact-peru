using Microsoft.EntityFrameworkCore;
using SecureFact.Billing.Contracts;
using SecureFact.Billing.Domain;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Billing.Infrastructure;

internal sealed class BillingDbContext(DbContextOptions<BillingDbContext> options, IDataScope scope) : TenantDbContext(options, scope)
{
    public const string Schema = "billing";

    public DbSet<Series> Series => Set<Series>();

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<DocumentLine> DocumentLines => Set<DocumentLine>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Series>(b =>
        {
            b.ToTable("series");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(s => s.CompanyId).HasColumnName("company_id");
            b.Property(s => s.EstablishmentId).HasColumnName("establishment_id");
            b.Property(s => s.DocumentTypeCode).HasColumnName("document_type_code").HasMaxLength(2).IsRequired();
            b.Property(s => s.Code).HasColumnName("code").HasMaxLength(4).IsRequired();
            b.Property(s => s.LastNumber).HasColumnName("last_number");
            b.Property(s => s.IsActive).HasColumnName("is_active");
            b.Property(s => s.CreatedAt).HasColumnName("created_at");
            b.Property(s => s.UpdatedAt).HasColumnName("updated_at");
            b.HasIndex(s => new { s.TenantId, s.CompanyId, s.DocumentTypeCode, s.Code }).IsUnique();
            b.ToTable(t => t.HasCheckConstraint("ck_series_last_number", "last_number >= 0 AND last_number <= 99999999"));
            ConfigureTenantOwned(b);
        });

        modelBuilder.Entity<Document>(b =>
        {
            b.ToTable("document");
            b.HasKey(d => d.Id);
            b.Property(d => d.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(d => d.CompanyId).HasColumnName("company_id");
            b.Property(d => d.SeriesId).HasColumnName("series_id");
            b.Property(d => d.DocumentTypeCode).HasColumnName("document_type_code").HasMaxLength(2).IsRequired();
            b.Property(d => d.SeriesCode).HasColumnName("series_code").HasMaxLength(4).IsRequired();
            b.Property(d => d.Number).HasColumnName("number");
            b.Property(d => d.IssueDate).HasColumnName("issue_date");
            b.Property(d => d.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(d => d.BuyerDocumentTypeCode).HasColumnName("buyer_document_type").HasMaxLength(2).IsRequired();
            b.Property(d => d.BuyerDocumentNumber).HasColumnName("buyer_document_number").HasMaxLength(20).IsRequired();
            b.Property(d => d.BuyerName).HasColumnName("buyer_name").HasMaxLength(250).IsRequired();
            b.Property(d => d.BuyerAddress).HasColumnName("buyer_address").HasMaxLength(250);
            b.Property(d => d.BuyerEmail).HasColumnName("buyer_email").HasMaxLength(254);
            b.Property(d => d.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(30).IsRequired();
            b.Property(d => d.PayableAmount).HasColumnName("payable_amount").HasPrecision(14, 2);
            b.Property(d => d.TotalsJson).HasColumnName("totals").HasColumnType("jsonb").IsRequired();
            b.Property(d => d.OriginalRequestJson).HasColumnName("original_request").HasColumnType("jsonb").IsRequired();
            b.Property(d => d.RequestHash).HasColumnName("request_hash").IsRequired();
            b.Property(d => d.CreatedAt).HasColumnName("created_at");
            b.HasIndex(d => new { d.TenantId, d.CompanyId, d.DocumentTypeCode, d.SeriesCode, d.Number }).IsUnique();
            b.HasIndex(d => new { d.TenantId, d.CompanyId, d.IssueDate });
            b.HasMany(d => d.Lines).WithOne().HasForeignKey(l => l.DocumentId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(d => d.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
            ConfigureTenantOwned(b);
        });

        modelBuilder.Entity<DocumentLine>(b =>
        {
            b.ToTable("document_line");
            b.HasKey(l => l.Id);
            b.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(l => l.DocumentId).HasColumnName("document_id");
            b.Property(l => l.LineNumber).HasColumnName("line_number");
            b.Property(l => l.Description).HasColumnName("description").HasMaxLength(500).IsRequired();
            b.Property(l => l.UnitCode).HasColumnName("unit_code").HasMaxLength(3).IsRequired();
            b.Property(l => l.ProductCode).HasColumnName("product_code").HasMaxLength(50);
            b.Property(l => l.Quantity).HasColumnName("quantity").HasPrecision(22, 10);
            b.Property(l => l.UnitValue).HasColumnName("unit_value").HasPrecision(22, 10);
            b.Property(l => l.AffectationCode).HasColumnName("affectation_code").HasMaxLength(2).IsRequired();
            b.Property(l => l.LineExtensionAmount).HasColumnName("line_extension_amount").HasPrecision(14, 2);
            b.Property(l => l.TaxCode).HasColumnName("tax_code").HasMaxLength(4).IsRequired();
            b.Property(l => l.TotalTaxAmount).HasColumnName("total_tax_amount").HasPrecision(14, 2);
            b.Property(l => l.UnitPriceIncludingTaxes).HasColumnName("unit_price_including_taxes").HasPrecision(22, 10);
            b.HasIndex(l => new { l.DocumentId, l.LineNumber }).IsUnique();
            ConfigureTenantOwned(b);
        });

        modelBuilder.Entity<IdempotencyRecord>(b =>
        {
            b.ToTable("idempotency_key");
            b.HasKey(r => r.Id);
            b.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(r => r.Key).HasColumnName("key").HasMaxLength(100).IsRequired();
            b.Property(r => r.RequestHash).HasColumnName("request_hash").IsRequired();
            b.Property(r => r.DocumentId).HasColumnName("document_id");
            b.Property(r => r.CreatedAt).HasColumnName("created_at");
            b.HasIndex(r => new { r.TenantId, r.Key }).IsUnique();
            ConfigureTenantOwned(b);
        });
    }
}
