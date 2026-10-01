using Microsoft.EntityFrameworkCore;
using SecureFact.Catalogs.Domain;

namespace SecureFact.Catalogs.Infrastructure;

internal sealed class CatalogsDbContext(DbContextOptions<CatalogsDbContext> options) : DbContext(options)
{
    public const string Schema = "catalog";

    public DbSet<CatalogEdition> Editions => Set<CatalogEdition>();

    public DbSet<CatalogEntry> Entries => Set<CatalogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<CatalogEdition>(b =>
        {
            b.ToTable("catalog_edition");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(e => e.CatalogNumber).HasColumnName("catalog_number").HasMaxLength(3).IsRequired();
            b.Property(e => e.Name).HasColumnName("name").HasMaxLength(300).IsRequired();
            b.Property(e => e.Version).HasColumnName("version");
            b.Property(e => e.Source).HasColumnName("source").HasMaxLength(200).IsRequired();
            b.Property(e => e.SourceSha256).HasColumnName("source_sha256").HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            b.Property(e => e.EffectiveTo).HasColumnName("effective_to");
            b.Property(e => e.LoadedAt).HasColumnName("loaded_at");
            b.HasIndex(e => new { e.CatalogNumber, e.Version }).IsUnique();
            b.HasIndex(e => new { e.CatalogNumber, e.SourceSha256 }).IsUnique();
        });

        modelBuilder.Entity<CatalogEntry>(b =>
        {
            b.ToTable("catalog_entry");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(e => e.CatalogNumber).HasColumnName("catalog_number").HasMaxLength(3).IsRequired();
            b.Property(e => e.Code).HasColumnName("code").HasMaxLength(60).IsRequired();
            b.Property(e => e.Description).HasColumnName("description").HasMaxLength(1000).IsRequired();
            b.Property(e => e.Version).HasColumnName("version");
            b.Property(e => e.EffectiveFrom).HasColumnName("effective_from");
            b.Property(e => e.EffectiveTo).HasColumnName("effective_to");
            b.Property(e => e.Source).HasColumnName("source").HasMaxLength(200).IsRequired();
            b.Property(e => e.Active).HasColumnName("active");
            b.Property(e => e.MetadataJson).HasColumnName("metadata").HasColumnType("jsonb").IsRequired();
            b.HasIndex(e => new { e.CatalogNumber, e.Code, e.Version }).IsUnique();
            b.HasIndex(e => new { e.CatalogNumber, e.EffectiveFrom });
        });
    }
}
