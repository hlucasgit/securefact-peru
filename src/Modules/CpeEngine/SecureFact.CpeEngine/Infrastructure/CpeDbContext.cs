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

    public DbSet<ArchivedFile> ArchivedFiles => Set<ArchivedFile>();

    public DbSet<OutboxMessageRow> OutboxMessages => Set<OutboxMessageRow>();

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
            b.Property(e => e.ReferenceDocumentId).HasColumnName("reference_document_id");
            b.Property(e => e.ReferenceTypeCode).HasColumnName("reference_type_code").HasMaxLength(2);
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
            b.Ignore(e => e.IsVoidCommunication);
            b.Ignore(e => e.IsTicketBatch);
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
            b.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(100);
            b.Property(e => e.LineStatus).HasColumnName("line_status").HasDefaultValue(1);
            b.Property(e => e.ReleasedAt).HasColumnName("released_at");
            b.HasIndex(e => e.SummaryId);

            // A receipt belongs to at most one active summary: two concurrent summaries cannot both report it.
            b.HasIndex(e => new { e.TenantId, e.ElectronicDocumentId, e.LineStatus }).IsUnique().HasFilter("released_at IS NULL").HasDatabaseName("ux_summary_item_active");
            ConfigureTenantOwned(b);
        });

        modelBuilder.Entity<ArchivedFile>(b =>
        {
            b.ToTable("archived_file");
            b.HasKey(f => f.Id);
            b.Property(f => f.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(f => f.ElectronicDocumentId).HasColumnName("electronic_document_id");
            b.Property(f => f.Kind).HasColumnName("kind").HasMaxLength(20).IsRequired();
            b.Property(f => f.StorageKey).HasColumnName("storage_key").HasMaxLength(900).IsRequired();
            b.Property(f => f.VersionId).HasColumnName("version_id").HasMaxLength(200);
            b.Property(f => f.Sha256).HasColumnName("sha256").HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(f => f.SizeBytes).HasColumnName("size_bytes");
            b.Property(f => f.ContentType).HasColumnName("content_type").HasMaxLength(100).IsRequired();
            b.Property(f => f.StoredAt).HasColumnName("stored_at");

            // One file of each kind per document: the archive is idempotent and a document is never archived twice.
            b.HasIndex(f => new { f.ElectronicDocumentId, f.Kind }).IsUnique();
            ConfigureTenantOwned(b);
        });

        modelBuilder.Entity<OutboxMessageRow>(b =>
        {
            b.MapOutboxMessage();
            ConfigureTenantOwned(b);
        });
    }
}
