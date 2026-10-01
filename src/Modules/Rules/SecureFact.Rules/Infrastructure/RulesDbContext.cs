using Microsoft.EntityFrameworkCore;
using SecureFact.Rules.Domain;

namespace SecureFact.Rules.Infrastructure;

internal sealed class RulesDbContext(DbContextOptions<RulesDbContext> options) : DbContext(options)
{
    public const string Schema = "rules";

    public DbSet<RuleVersion> RuleVersions => Set<RuleVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<RuleVersion>(b =>
        {
            b.ToTable("rule_version");
            b.HasKey(r => r.Id);
            b.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(r => r.Code).HasColumnName("code").HasMaxLength(100).IsRequired();
            b.Property(r => r.Version).HasColumnName("version");
            b.Property(r => r.EffectiveFrom).HasColumnName("effective_from");
            b.Property(r => r.EffectiveTo).HasColumnName("effective_to");
            b.Property(r => r.ConfigurationJson).HasColumnName("configuration").HasColumnType("jsonb").IsRequired();
            b.Property(r => r.Source).HasColumnName("source").HasMaxLength(1000).IsRequired();
            b.Property(r => r.Verification).HasColumnName("verification").HasConversion<string>().HasMaxLength(20).IsRequired();
            b.HasIndex(r => new { r.Code, r.Version }).IsUnique();
            b.HasIndex(r => new { r.Code, r.EffectiveFrom });
        });
    }
}
