using SecureFact.Platform.Persistence;

namespace SecureFact.Certificates.Domain;

internal sealed class CompanyCertificate : ITenantOwned
{
    private CompanyCertificate()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid CompanyId { get; private set; }

    public string Subject { get; private set; } = string.Empty;

    public string Thumbprint { get; private set; } = string.Empty;

    public string SerialNumber { get; private set; } = string.Empty;

    public DateTimeOffset NotBefore { get; private set; }

    public DateTimeOffset NotAfter { get; private set; }

    /// <summary>PKCS#12 (key included) encrypted by <c>ISecretProtector</c>. Never exposed outside the module.</summary>
    public byte[] ProtectedPfx { get; private set; } = [];

    public bool IsActive { get; private set; }

    public bool RucInSubject { get; private set; }

    public Guid? UploadedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? DeactivatedAt { get; private set; }

    public uint Version { get; private set; }

    public static CompanyCertificate Create(
        Guid id, Guid tenantId, Guid companyId, string subject, string thumbprint, string serialNumber,
        DateTimeOffset notBefore, DateTimeOffset notAfter, byte[] protectedPfx, bool rucInSubject, Guid? uploadedBy, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        CompanyId = companyId,
        Subject = subject,
        Thumbprint = thumbprint,
        SerialNumber = serialNumber,
        NotBefore = notBefore,
        NotAfter = notAfter,
        ProtectedPfx = protectedPfx,
        IsActive = true,
        RucInSubject = rucInSubject,
        UploadedBy = uploadedBy,
        CreatedAt = now,
    };

    public void Deactivate(DateTimeOffset now)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        DeactivatedAt = now;
    }
}
