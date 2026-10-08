using SecureFact.Platform.Persistence;

namespace SecureFact.Gre.Domain;

/// <summary>A series of the guides of the sender of a company: «T» and three more characters. The numbers are taken in the database, never as the maximum plus one.</summary>
internal sealed class GreSeries : ITenantOwned
{
    public const long MaxNumber = 99_999_999;

    private GreSeries()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public long LastNumber { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    public static GreSeries Create(Guid id, Guid tenantId, Guid companyId, string code, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        CompanyId = companyId,
        Code = code,
        IsActive = true,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        UpdatedAt = now;
    }
}
