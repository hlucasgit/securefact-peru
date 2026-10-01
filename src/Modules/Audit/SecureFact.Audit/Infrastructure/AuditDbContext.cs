using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Domain;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Audit.Infrastructure;

internal sealed class AuditDbContext(DbContextOptions<AuditDbContext> options, IDataScope scope) : TenantDbContext(options, scope)
{
    public const string Schema = "audit";

    public DbSet<AuditEventEntity> Events => Set<AuditEventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<AuditEventEntity>(b =>
        {
            b.ToTable("audit_event");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(e => e.ChainKey).HasColumnName("chain_key").HasMaxLength(40).IsRequired();
            b.Property(e => e.Sequence).HasColumnName("seq");
            b.Property(e => e.OccurredAt).HasColumnName("occurred_at");
            b.Property(e => e.ActorType).HasColumnName("actor_type").HasMaxLength(20).IsRequired();
            b.Property(e => e.ActorUserId).HasColumnName("actor_user_id");
            b.Property(e => e.Action).HasColumnName("action").HasMaxLength(120).IsRequired();
            b.Property(e => e.EntityType).HasColumnName("entity_type").HasMaxLength(80).IsRequired();
            b.Property(e => e.EntityId).HasColumnName("entity_id").HasMaxLength(80);
            b.Property(e => e.OldValues).HasColumnName("old_values");
            b.Property(e => e.NewValues).HasColumnName("new_values");
            b.Property(e => e.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            b.Property(e => e.UserAgent).HasColumnName("user_agent").HasMaxLength(300);
            b.Property(e => e.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            b.Property(e => e.RequestId).HasColumnName("request_id").HasMaxLength(64);
            b.Property(e => e.PreviousHash).HasColumnName("prev_hash").IsRequired();
            b.Property(e => e.Hash).HasColumnName("hash").IsRequired();
            b.HasIndex(e => new { e.ChainKey, e.Sequence }).IsUnique();
            b.HasIndex(e => new { e.TenantId, e.Action });
            b.HasIndex(e => new { e.EntityType, e.EntityId });
            ConfigureOptionalTenantOwned(b);
        });
    }
}
