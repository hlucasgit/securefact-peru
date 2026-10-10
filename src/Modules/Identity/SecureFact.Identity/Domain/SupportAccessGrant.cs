using SecureFact.Platform.Persistence;

namespace SecureFact.Identity.Domain;

/// <summary>What the owner of an account allowed: the people who support it may read it until <see cref="ExpiresAt"/> (ADR-069). It is never extended: another one is granted.</summary>
internal sealed class SupportAccessGrant : ITenantOwned
{
    private SupportAccessGrant()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid? GrantedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public Guid? RevokedBy { get; private set; }

    public string? Note { get; private set; }

    public uint Version { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public static SupportAccessGrant Create(Guid tenantId, Guid? grantedBy, DateTimeOffset now, DateTimeOffset expiresAt, string? note) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        GrantedBy = grantedBy,
        CreatedAt = now,
        ExpiresAt = expiresAt,
        Note = note,
    };

    public void Revoke(Guid? by, DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevokedBy = by;
    }
}
