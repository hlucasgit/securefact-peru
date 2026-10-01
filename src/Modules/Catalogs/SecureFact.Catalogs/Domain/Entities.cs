namespace SecureFact.Catalogs.Domain;

/// <summary>One loaded edition of an official catalogue. Loading the same source file twice is a no-op (matched by SHA-256).</summary>
internal sealed class CatalogEdition
{
    public Guid Id { get; set; }

    public string CatalogNumber { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Version { get; set; }

    public string Source { get; set; } = string.Empty;

    public string SourceSha256 { get; set; } = string.Empty;

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public DateTimeOffset LoadedAt { get; set; }
}

internal sealed class CatalogEntry
{
    public Guid Id { get; set; }

    public string CatalogNumber { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public int Version { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    public string Source { get; set; } = string.Empty;

    public bool Active { get; set; }

    /// <summary>Extra columns of the source table (e.g. international tax code), as JSON.</summary>
    public string MetadataJson { get; set; } = "{}";
}
