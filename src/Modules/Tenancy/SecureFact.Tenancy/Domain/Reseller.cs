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

    public void SetHost(string? host) => Host = host;

    public void SetLogo(byte[]? data, string? contentType, string? version)
    {
        Logo = data;
        LogoContentType = contentType;
        LogoVersion = version;
    }
}
