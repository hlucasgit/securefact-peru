using Microsoft.EntityFrameworkCore;
using SecureFact.Notifications.Domain;

namespace SecureFact.Notifications.Infrastructure;

/// <summary>The queue of e-mails belongs to the platform: only the explicit platform scope reads or writes it (RLS platform_only).</summary>
internal sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public const string Schema = "notifications";

    public DbSet<QueuedEmail> Emails => Set<QueuedEmail>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<QueuedEmail>(builder =>
        {
            builder.ToTable("email_queue");
            builder.HasKey(e => e.Id);
            builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            builder.Property(e => e.ToAddress).HasColumnName("to_address").HasMaxLength(254).IsRequired();
            builder.Property(e => e.Subject).HasColumnName("subject").HasMaxLength(300).IsRequired();
            builder.Property(e => e.TextBody).HasColumnName("text_body").IsRequired();
            builder.Property(e => e.HtmlBody).HasColumnName("html_body").IsRequired();
            builder.Property(e => e.FromName).HasColumnName("from_name").HasMaxLength(120);
            builder.Property(e => e.ReplyTo).HasColumnName("reply_to").HasMaxLength(254);
            builder.Property(e => e.Attempts).HasColumnName("attempts").IsRequired();
            builder.Property(e => e.NextAttemptAt).HasColumnName("next_attempt_at").IsRequired();
            builder.Property(e => e.LockedUntil).HasColumnName("locked_until");
            builder.Property(e => e.SentAt).HasColumnName("sent_at");
            builder.Property(e => e.DeadAt).HasColumnName("dead_at");
            builder.Property(e => e.LastError).HasColumnName("last_error").HasMaxLength(500);

            // The dispatcher scans only what is pending: due, not sent and not dead.
            builder.HasIndex(e => e.NextAttemptAt).HasFilter("sent_at IS NULL AND dead_at IS NULL").HasDatabaseName("ix_email_queue_pending");
        });
    }
}
