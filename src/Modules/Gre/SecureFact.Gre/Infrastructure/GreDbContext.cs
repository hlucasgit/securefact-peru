using Microsoft.EntityFrameworkCore;
using SecureFact.Gre.Domain;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Gre.Infrastructure;

internal sealed class GreDbContext(DbContextOptions<GreDbContext> options, IDataScope scope) : TenantDbContext(options, scope)
{
    public const string Schema = "gre";

    public DbSet<GreSeries> Series => Set<GreSeries>();

    public DbSet<Guide> Guides => Set<Guide>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<GreSeries>(b =>
        {
            b.ToTable("series");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(s => s.CompanyId).HasColumnName("company_id");
            b.Property(s => s.Code).HasColumnName("code").HasMaxLength(4).IsRequired();
            b.Property(s => s.LastNumber).HasColumnName("last_number");
            b.Property(s => s.IsActive).HasColumnName("is_active");
            b.Property(s => s.CreatedAt).HasColumnName("created_at");
            b.Property(s => s.UpdatedAt).HasColumnName("updated_at");
            b.Property(s => s.Version).IsRowVersion();
            b.HasIndex(s => new { s.TenantId, s.CompanyId, s.Code }).IsUnique();
            ConfigureTenantOwned(b);
        });

        modelBuilder.Entity<Guide>(b =>
        {
            b.ToTable("guide");
            b.HasKey(g => g.Id);
            b.Property(g => g.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(g => g.CompanyId).HasColumnName("company_id");
            b.Property(g => g.SeriesId).HasColumnName("series_id");
            b.Property(g => g.Series).HasColumnName("series").HasMaxLength(4).IsRequired();
            b.Property(g => g.Number).HasColumnName("number");
            b.Property(g => g.IssueDate).HasColumnName("issue_date");
            b.Property(g => g.MotiveCode).HasColumnName("motive_code").HasMaxLength(2).IsRequired();
            b.Property(g => g.ModalityCode).HasColumnName("modality_code").HasMaxLength(2).IsRequired();
            b.Property(g => g.RecipientDocument).HasColumnName("recipient_document").HasMaxLength(20).IsRequired();
            b.Property(g => g.RecipientName).HasColumnName("recipient_name").HasMaxLength(250).IsRequired();
            b.Property(g => g.RequestJson).HasColumnName("request").HasColumnType("jsonb").IsRequired();
            b.Property(g => g.FileBaseName).HasColumnName("file_base_name").HasMaxLength(60).IsRequired();
            b.Property(g => g.SignedXml).HasColumnName("signed_xml").IsRequired();
            b.Property(g => g.DigestValue).HasColumnName("digest_value").HasMaxLength(100).IsRequired();
            b.Property(g => g.State).HasColumnName("state").HasConversion<string>().HasMaxLength(30);
            b.Property(g => g.Ticket).HasColumnName("ticket").HasMaxLength(100);
            b.Property(g => g.Attempts).HasColumnName("attempts");
            b.Property(g => g.NextAttemptAt).HasColumnName("next_attempt_at");
            b.Property(g => g.CdrZip).HasColumnName("cdr_zip");
            b.Property(g => g.CdrProcessId).HasColumnName("cdr_process_id").HasMaxLength(100);
            b.Property(g => g.CdrResponseCode).HasColumnName("cdr_response_code");
            b.Property(g => g.CdrDescription).HasColumnName("cdr_description").HasMaxLength(1000);
            b.Property(g => g.CdrObservationsJson).HasColumnName("cdr_observations").HasColumnType("jsonb");
            b.Property(g => g.ErrorCode).HasColumnName("error_code").HasMaxLength(40);
            b.Property(g => g.ErrorMessage).HasColumnName("error_message").HasMaxLength(500);
            b.Property(g => g.CreatedAt).HasColumnName("created_at");
            b.Property(g => g.UpdatedAt).HasColumnName("updated_at");
            b.Property(g => g.SentAt).HasColumnName("sent_at");
            b.Property(g => g.ProcessedAt).HasColumnName("processed_at");
            b.Property(g => g.Version).IsRowVersion();
            b.Ignore(g => g.IsFinal);
            b.HasIndex(g => new { g.TenantId, g.CompanyId, g.Series, g.Number }).IsUnique();
            b.HasIndex(g => new { g.State, g.NextAttemptAt });
            b.HasIndex(g => new { g.TenantId, g.CreatedAt });
            ConfigureTenantOwned(b);
        });
    }
}
