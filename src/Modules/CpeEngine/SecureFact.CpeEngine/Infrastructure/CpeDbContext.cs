using Microsoft.EntityFrameworkCore;
using SecureFact.CpeEngine.Domain;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;

namespace SecureFact.CpeEngine.Infrastructure;

internal sealed class CpeDbContext(DbContextOptions<CpeDbContext> options, IDataScope scope) : TenantDbContext(options, scope)
{
    public const string Schema = "cpe";

    public DbSet<ElectronicDocument> ElectronicDocuments => Set<ElectronicDocument>();

    public DbSet<ElectronicDocumentEvent> Events => Set<ElectronicDocumentEvent>();

    public DbSet<SummaryItem> SummaryItems => Set<SummaryItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ElectronicDocument>(b =>
        {
            b.ToTable("electronic_document");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(e => e.DocumentId).HasColumnName("document_id");
            b.Property(e => e.CompanyId).HasColumnName("company_id");
            b.Property(e => e.IssueDate).HasColumnName("issue_date");
            b.Property(e => e.DocumentTypeCode).HasColumnName("document_type_code").HasMaxLength(2).IsRequired();
            b.Property(e => e.Series).HasColumnName("series").HasMaxLength(4).IsRequired();
            b.Property(e => e.Number).HasColumnName("number");
            b.Property(e => e.FileBaseName).HasColumnName("file_base_name").HasMaxLength(80).IsRequired();
            b.Property(e => e.State).HasColumnName("state").HasConversion<string>().HasMaxLength(30);
            b.Property(e => e.Attempts).HasColumnName("attempts");
            b.Property(e => e.SignedXml).HasColumnName("signed_xml").IsRequired();
            b.Property(e => e.DigestValue).HasColumnName("digest_value").HasMaxLength(100).IsRequired();
            b.Property(e => e.Ticket).HasColumnName("ticket").HasMaxLength(100);
            b.Property(e => e.CdrZip).HasColumnName("cdr_zip");
            b.Property(e => e.CdrProcessId).HasColumnName("cdr_process_id").HasMaxLength(100);
            b.Property(e => e.CdrResponseCode).HasColumnName("cdr_response_code");
            b.Property(e => e.CdrDescription).HasColumnName("cdr_description").HasMaxLength(1000);
            b.Property(e => e.CdrObservationsJson).HasColumnName("cdr_observations").HasColumnType("jsonb");
            b.Property(e => e.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(40);
            b.Property(e => e.LastErrorMessage).HasColumnName("last_error_message").HasMaxLength(500);
            b.Property(e => e.NextAttemptAt).HasColumnName("next_attempt_at");
            b.Property(e => e.CreatedAt).HasColumnName("created_at");
            b.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            b.Property(e => e.SentAt).HasColumnName("sent_at");
            b.Property(e => e.ProcessedAt).HasColumnName("processed_at");
            b.Property(e => e.Version).IsRowVersion();
            b.Ignore(e => e.Snapshot);
            b.Ignore(e => e.IsSummary);
            b.HasIndex(e => new { e.TenantId, e.DocumentId }).IsUnique();
            b.HasIndex(e => new { e.TenantId, e.FileBaseName }).IsUnique();
            b.HasIndex(e => new { e.State, e.NextAttemptAt });
            ConfigureTenantOwned(b);
        });

        modelBuilder.Entity<ElectronicDocumentEvent>(b =>
        {
            b.ToTable("electronic_document_event");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(e => e.ElectronicDocumentId).HasColumnName("electronic_document_id");
            b.Property(e => e.FromState).HasColumnName("from_state").HasConversion<string>().HasMaxLength(30);
            b.Property(e => e.ToState).HasColumnName("to_state").HasConversion<string>().HasMaxLength(30);
            b.Property(e => e.Event).HasColumnName("event").HasConversion<string>().HasMaxLength(40);
            b.Property(e => e.Attempt).HasColumnName("attempt");
            b.Property(e => e.Detail).HasColumnName("detail").HasMaxLength(500);
            b.Property(e => e.OccurredAt).HasColumnName("occurred_at");
            b.HasIndex(e => new { e.ElectronicDocumentId, e.OccurredAt });
            ConfigureTenantOwned(b);
        });

        modelBuilder.Entity<SummaryItem>(b =>
        {
            b.ToTable("summary_item");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(e => e.SummaryId).HasColumnName("summary_id");
            b.Property(e => e.ElectronicDocumentId).HasColumnName("electronic_document_id");
            b.Property(e => e.LineNumber).HasColumnName("line_number");
            b.Property(e => e.ReleasedAt).HasColumnName("released_at");
            b.HasIndex(e => e.SummaryId);

            // A receipt belongs to at most one active summary: two concurrent summaries cannot both report it.
            b.HasIndex(e => new { e.TenantId, e.ElectronicDocumentId }).IsUnique().HasFilter("released_at IS NULL").HasDatabaseName("ux_summary_item_active");
            ConfigureTenantOwned(b);
        });
    }
}
