using SecureFact.Platform.Persistence;

namespace SecureFact.Gre.Domain;

/// <summary>A series of the guides of the sender of a company: «T» and three more characters. The numbers are taken in the database, never as the maximum plus one.</summary>
/// <summary>The two types of guide that this module issues.</summary>
internal static class DocumentTypes
{
    public const string Sender = "09";
    public const string Carrier = "31";
}

internal sealed class GreSeries : ITenantOwned
{
    public const long MaxNumber = 99_999_999;

    private GreSeries()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid CompanyId { get; private set; }

    /// <summary><c>09</c> guide of the sender (series «T…») or <c>31</c> guide of the carrier (series «V…»).</summary>
    public string DocumentTypeCode { get; private set; } = DocumentTypes.Sender;

    public string Code { get; private set; } = string.Empty;

    public long LastNumber { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    public static GreSeries Create(Guid id, Guid tenantId, Guid companyId, string documentTypeCode, string code, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        CompanyId = companyId,
        DocumentTypeCode = documentTypeCode,
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
