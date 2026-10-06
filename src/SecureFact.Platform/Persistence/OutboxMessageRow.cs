using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SecureFact.Platform.Persistence;

/// <summary>
/// A row of a module's <c>outbox_message</c> table (ADR-022): an integration event stored in the transaction that caused it. Immutable except for its delivery bookkeeping (database trigger).
/// A module adds it to its own context and maps it with <see cref="OutboxMessageMapping.MapOutboxMessage"/>; the dispatcher reads it through <c>PostgresOutboxSource</c>.
/// </summary>
public sealed class OutboxMessageRow : ITenantOwned
{
    private OutboxMessageRow()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public string PayloadJson { get; private set; } = "{}";

    public DateTimeOffset CreatedAt { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public DateTimeOffset? DeadAt { get; private set; }

    public string? LastError { get; private set; }

    public static OutboxMessageRow Create(Guid tenantId, string eventType, string payloadJson, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        EventType = eventType,
        PayloadJson = payloadJson,
        CreatedAt = now,
        NextAttemptAt = now,
    };
}

public static class OutboxMessageMapping
{
    /// <summary>The columns of <c>outbox_message</c>. The module still calls its own <c>ConfigureTenantOwned</c> for the tenant column and the filter.</summary>
    public static void MapOutboxMessage(this EntityTypeBuilder<OutboxMessageRow> b)
    {
        b.ToTable("outbox_message");
        b.HasKey(m => m.Id);
        b.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(m => m.EventType).HasColumnName("event_type").HasMaxLength(100).IsRequired();
        b.Property(m => m.PayloadJson).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        b.Property(m => m.CreatedAt).HasColumnName("created_at");
        b.Property(m => m.Attempts).HasColumnName("attempts");
        b.Property(m => m.NextAttemptAt).HasColumnName("next_attempt_at");
        b.Property(m => m.LockedUntil).HasColumnName("locked_until");
        b.Property(m => m.ProcessedAt).HasColumnName("processed_at");
        b.Property(m => m.DeadAt).HasColumnName("dead_at");
        b.Property(m => m.LastError).HasColumnName("last_error").HasMaxLength(500);

        // The dispatcher scans only what is pending: due, not processed and not dead.
        b.HasIndex(m => m.NextAttemptAt).HasFilter("processed_at IS NULL AND dead_at IS NULL").HasDatabaseName("ix_outbox_message_pending");
    }
}
