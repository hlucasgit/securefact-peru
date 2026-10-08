using SecureFact.Platform.Persistence;

namespace SecureFact.Certificates.Domain;

internal sealed class SolCredential : ITenantOwned
{
    private SolCredential()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid CompanyId { get; private set; }

    public string SolUser { get; private set; } = string.Empty;

    /// <summary>Encrypted by <c>ISecretProtector</c>; null once cleared.</summary>
    public byte[]? ProtectedPassword { get; private set; }

    /// <summary>The <c>client_id</c> of the application registered in SOL for the API of the GRE; null while none is.</summary>
    public string? ApiClientId { get; private set; }

    /// <summary>The <c>client_secret</c>, encrypted by <c>ISecretProtector</c>.</summary>
    public byte[]? ProtectedApiClientSecret { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    public static SolCredential Create(Guid id, Guid tenantId, Guid companyId, string solUser, byte[] protectedPassword, Guid? by, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        CompanyId = companyId,
        SolUser = solUser,
        ProtectedPassword = protectedPassword,
        UpdatedBy = by,
        UpdatedAt = now,
    };

    public void Replace(string solUser, byte[] protectedPassword, Guid? by, DateTimeOffset now)
    {
        SolUser = solUser;
        ProtectedPassword = protectedPassword;
        UpdatedBy = by;
        UpdatedAt = now;
    }

    public void SetApi(string clientId, byte[] protectedSecret, Guid? by, DateTimeOffset now)
    {
        ApiClientId = clientId;
        ProtectedApiClientSecret = protectedSecret;
        UpdatedBy = by;
        UpdatedAt = now;
    }

    public void ClearApi(Guid? by, DateTimeOffset now)
    {
        ApiClientId = null;
        ProtectedApiClientSecret = null;
        UpdatedBy = by;
        UpdatedAt = now;
    }

    public void Clear(Guid? by, DateTimeOffset now)
    {
        ProtectedPassword = null;
        UpdatedBy = by;
        UpdatedAt = now;
    }
}
