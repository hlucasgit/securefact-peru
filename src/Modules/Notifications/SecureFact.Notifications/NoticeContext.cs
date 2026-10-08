using SecureFact.SharedKernel.Domain;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Notifications;

/// <summary>Settings of the public address of the web interface (<c>Web</c> section), for the links that go out in e-mails.</summary>
public sealed class WebOptions
{
    public const string SectionName = "Web";

    public const string DevelopmentUrl = "http://localhost:5173";

    /// <summary>The address of the platform's own portal, with no path. A reseller with a verified domain gets its own address instead.</summary>
    public string? PublicUrl { get; set; }
}

/// <summary>Who an e-mail is signed by and where its links lead (ADR-044, ADR-052, ADR-054): the brand and the verified portal of the reseller of the account, or the platform's own.</summary>
public sealed class NoticeContext(IBranding branding, WebOptions web)
{
    public async Task<(EmailBrand Brand, string PortalUrl, Guid? BrandedReseller)> ForAsync(Guid? tenantId, Guid? resellerId, CancellationToken cancellationToken)
    {
        var brand = resellerId is { } reseller
            ? await branding.ForResellerAsync(reseller, cancellationToken)
            : tenantId is { } tenant ? await branding.ForTenantAsync(new TenantId(tenant), cancellationToken) : null;
        var portal = brand is not null && await branding.PortalHostAsync(brand.ResellerId, cancellationToken) is { } host
            ? $"https://{host}"
            : (web.PublicUrl ?? WebOptions.DevelopmentUrl).TrimEnd('/');
        return (brand is null ? EmailBrand.Platform : new EmailBrand(brand.BrandName, brand.SupportEmail), portal, brand?.ResellerId);
    }
}
