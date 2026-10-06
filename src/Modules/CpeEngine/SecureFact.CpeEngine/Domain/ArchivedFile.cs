using SecureFact.Platform.Persistence;

namespace SecureFact.CpeEngine.Domain;

/// <summary>
/// A file of an electronic document that lives in object storage (ADR-005, ADR-036): where it is, its hash and size. Insert-only (database trigger): an archived file is never replaced,
/// and the hash is what a later read is checked against.
/// </summary>
internal sealed class ArchivedFile : ITenantOwned
{
    private ArchivedFile()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ElectronicDocumentId { get; private set; }

    public string Kind { get; private set; } = string.Empty;

    public string StorageKey { get; private set; } = string.Empty;

    /// <summary>Version the store assigned to the object; null when the bucket does not keep versions.</summary>
    public string? VersionId { get; private set; }

    public string Sha256 { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    public string ContentType { get; private set; } = string.Empty;

    public DateTimeOffset StoredAt { get; private set; }

    public static ArchivedFile Create(Guid tenantId, Guid electronicDocumentId, string kind, string storageKey, string? versionId, string sha256, long sizeBytes, string contentType, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ElectronicDocumentId = electronicDocumentId,
        Kind = kind,
        StorageKey = storageKey,
        VersionId = versionId,
        Sha256 = sha256,
        SizeBytes = sizeBytes,
        ContentType = contentType,
        StoredAt = now,
    };
}
