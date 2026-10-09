using SecureFact.Platform.Persistence;

namespace SecureFact.Identity.Domain;

internal sealed class ApiKey : ITenantOwned
{
    private ApiKey()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Role { get; private set; } = string.Empty;

    /// <summary>SHA-256 of the secret. The secret has 256 bits of entropy, so a plain hash is enough: there is nothing to guess.</summary>
    public byte[] SecretHash { get; private set; } = [];

    public string Prefix { get; private set; } = string.Empty;

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public uint Version { get; private set; }

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);

    public static ApiKey Create(Guid id, Guid tenantId, string name, string role, byte[] secretHash, string prefix, Guid? createdBy, DateTimeOffset now, DateTimeOffset? expiresAt) => new()
    {
        Id = id,
        TenantId = tenantId,
        Name = name,
        Role = role,
        SecretHash = secretHash,
        Prefix = prefix,
        CreatedBy = createdBy,
        CreatedAt = now,
        ExpiresAt = expiresAt,
    };

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
