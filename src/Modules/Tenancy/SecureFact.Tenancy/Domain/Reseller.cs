using SecureFact.Tenancy.Contracts;

namespace SecureFact.Tenancy.Domain;

/// <summary>A reseller: a company that brings tenants to the platform and manages them. It belongs to the platform, not to a tenant.</summary>
internal sealed class Reseller
{
    private Reseller()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>How the interface presents itself to the reseller's users. Null until the reseller sets it: the default look of the platform applies.</summary>
    public string? BrandName { get; private set; }

    /// <summary>Hex colour <c>#rrggbb</c> of the accents of the interface. It always has enough contrast against the white text that sits on it.</summary>
    public string? PrimaryColor { get; private set; }

    public string? SupportEmail { get; private set; }

    /// <summary>Host name (lower case, no port) at which the reseller's portal is served, so the sign-in page can show its brand before anyone signs in. Only the platform sets it.</summary>
    public string? Host { get; private set; }

    /// <summary>The state of <see cref="Host"/>: none, pending, verified or unreachable.</summary>
    public DomainStatus HostStatus { get; private set; }

    /// <summary>The secret that the reseller publishes in a TXT record to prove it controls the domain. New with every new domain.</summary>
    public string? HostToken { get; private set; }

    public DateTimeOffset? HostVerifiedAt { get; private set; }

    public DateTimeOffset? HostCheckedAt { get; private set; }

    /// <summary>What the last failed check found, in words the reseller can act on.</summary>
    public string? HostError { get; private set; }

    /// <summary>Failed checks in a row; a verified domain becomes unreachable after several.</summary>
    public int HostFailures { get; private set; }

    public byte[]? Logo { get; private set; }

    public string? LogoContentType { get; private set; }

    /// <summary>Changes with every logo, so a cached logo is never shown after a new one is uploaded.</summary>
    public string? LogoVersion { get; private set; }

    public uint Version { get; private set; }

    public static Reseller Create(Guid id, string name, DateTimeOffset now) => new() { Id = id, Name = name, IsActive = true, CreatedAt = now };

    public void Update(string name, bool isActive)
    {
        Name = name;
        IsActive = isActive;
    }

    public void SetBrand(string? brandName, string? primaryColor, string? supportEmail)
    {
        BrandName = brandName;
        PrimaryColor = primaryColor;
        SupportEmail = supportEmail;
    }

    public void SetHost(string? host, string? token)
    {
        Host = host;
        HostToken = host is null ? null : token;
        HostStatus = host is null ? DomainStatus.None : DomainStatus.Pending;
        HostVerifiedAt = null;
        HostCheckedAt = null;
        HostError = null;
        HostFailures = 0;
    }

    public void MarkVerified(DateTimeOffset now)
    {
        HostStatus = DomainStatus.Verified;
        HostVerifiedAt ??= now;
        HostCheckedAt = now;
        HostError = null;
        HostFailures = 0;
    }

    /// <summary>A failed check. A pending domain stays pending; a verified one becomes unreachable only after <paramref name="failuresToUnreachable"/> in a row, so a blip of the DNS does not take a portal down.</summary>
    public void MarkCheckFailed(DateTimeOffset now, string error, int failuresToUnreachable)
    {
        HostCheckedAt = now;
        HostError = error;
        HostFailures++;
        if (HostStatus == DomainStatus.Verified && HostFailures >= failuresToUnreachable)
        {
            HostStatus = DomainStatus.Unreachable;
            HostVerifiedAt = null;
        }
    }

    public void SetLogo(byte[]? data, string? contentType, string? version)
    {
        Logo = data;
        LogoContentType = contentType;
        LogoVersion = version;
    }
}
