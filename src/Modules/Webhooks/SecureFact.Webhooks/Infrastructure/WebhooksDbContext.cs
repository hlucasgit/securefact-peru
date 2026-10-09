using Microsoft.EntityFrameworkCore;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;
using SecureFact.Webhooks.Domain;

namespace SecureFact.Webhooks.Infrastructure;

internal sealed class WebhooksDbContext(DbContextOptions<WebhooksDbContext> options, IDataScope scope) : TenantDbContext(options, scope)
{
    public const string Schema = "webhook";

    public DbSet<WebhookEndpoint> Endpoints => Set<WebhookEndpoint>();

    public DbSet<WebhookDelivery> Deliveries => Set<WebhookDelivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<WebhookEndpoint>(b =>
        {
            b.ToTable("endpoint");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(e => e.Url).HasColumnName("url").HasMaxLength(500).IsRequired();
            b.Property(e => e.Description).HasColumnName("description").HasMaxLength(200);
            b.Property(e => e.Events).HasColumnName("events").HasColumnType("text[]");
            b.Property(e => e.SecretCiphertext).HasColumnName("secret_ciphertext").IsRequired();
            b.Property(e => e.SecretHint).HasColumnName("secret_hint").HasMaxLength(8).IsRequired();
            b.Property(e => e.IsActive).HasColumnName("is_active");
            b.Property(e => e.ConsecutiveFailures).HasColumnName("consecutive_failures");
            b.Property(e => e.DisabledAt).HasColumnName("disabled_at");
            b.Property(e => e.DisabledReason).HasColumnName("disabled_reason").HasMaxLength(200);
            b.Property(e => e.CreatedAt).HasColumnName("created_at");
            b.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            b.Property(e => e.Version).IsRowVersion();
            ConfigureTenantOwned(b);
        });

        modelBuilder.Entity<WebhookDelivery>(b =>
        {
            b.ToTable("delivery");
            b.HasKey(d => d.Id);
            b.Property(d => d.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(d => d.EndpointId).HasColumnName("endpoint_id");
            b.Property(d => d.EventId).HasColumnName("event_id");
            b.Property(d => d.EventType).HasColumnName("event_type").HasMaxLength(60).IsRequired();
            b.Property(d => d.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            b.Property(d => d.State).HasColumnName("state").HasConversion<string>().HasMaxLength(20);
            b.Property(d => d.Attempts).HasColumnName("attempts");
            b.Property(d => d.NextAttemptAt).HasColumnName("next_attempt_at");
            b.Property(d => d.LastStatusCode).HasColumnName("last_status_code");
            b.Property(d => d.LastError).HasColumnName("last_error").HasMaxLength(300);
            b.Property(d => d.CreatedAt).HasColumnName("created_at");
            b.Property(d => d.DeliveredAt).HasColumnName("delivered_at");
            b.Property(d => d.Version).IsRowVersion();
            b.HasIndex(d => new { d.EndpointId, d.EventId }).IsUnique();
            b.HasIndex(d => new { d.State, d.NextAttemptAt });
            b.HasIndex(d => new { d.EndpointId, d.CreatedAt });
            ConfigureTenantOwned(b);
        });
    }
}
