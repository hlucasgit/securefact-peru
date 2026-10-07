using System.Globalization;
using SecureFact.Identity.Contracts;
using SecureFact.Notifications;
using SecureFact.Notifications.Contracts;
using SecureFact.SharedKernel.Domain;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Api.Security;

/// <summary>Settings of the public address of the web interface (<c>Web</c> section), for the links that go out in e-mails.</summary>
public sealed class WebOptions
{
    public const string SectionName = "Web";

    public const string DevelopmentUrl = "http://localhost:5173";

    /// <summary>The address of the platform's own portal, with no path. A reseller with a verified domain gets its own address instead.</summary>
    public string? PublicUrl { get; set; }
}

/// <summary>
/// Delivers the link to choose a new password by e-mail, signed with the brand of the reseller of the account and pointing to its portal when it has a verified domain (ADR-052).
/// A failed delivery is logged without the token and is not reported to the caller: the answer of the request must be the same whether the address exists or not.
/// </summary>
internal sealed partial class EmailPasswordResetNotifier(
    IEmailSender email,
    IBranding branding,
    WebOptions web,
    TimeProvider clock,
    ILogger<EmailPasswordResetNotifier> logger) : IPasswordResetNotifier
{
    public async Task SendAsync(PasswordResetDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var brand = await BrandOfAsync(delivery, cancellationToken);
        var baseUrl = await PortalOfAsync(brand?.ResellerId, cancellationToken);
        var link = string.Create(CultureInfo.InvariantCulture, $"{baseUrl}/restablecer#token={Uri.EscapeDataString(delivery.Token)}");
        var minutes = Math.Max(1, (int)Math.Ceiling((delivery.ExpiresAt - clock.GetUtcNow()).TotalMinutes));
        var message = PasswordResetEmail.Compose(delivery.Email, brand is null ? EmailBrand.Platform : new EmailBrand(brand.BrandName, brand.SupportEmail), link, minutes);
        try
        {
            await email.SendAsync(message, cancellationToken);
        }
        catch (EmailDeliveryException failure)
        {
            LogNotDelivered(failure.Message);
        }
    }

    private async Task<BrandingDto?> BrandOfAsync(PasswordResetDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.ResellerId is { } resellerId)
        {
            return await branding.ForResellerAsync(resellerId, cancellationToken);
        }

        return delivery.TenantId is { } tenantId ? await branding.ForTenantAsync(new TenantId(tenantId), cancellationToken) : null;
    }

    private async Task<string> PortalOfAsync(Guid? resellerId, CancellationToken cancellationToken)
    {
        if (resellerId is { } id && await branding.PortalHostAsync(id, cancellationToken) is { } host)
        {
            return $"https://{host}";
        }

        return (web.PublicUrl ?? WebOptions.DevelopmentUrl).TrimEnd('/');
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A password reset e-mail was not delivered ({Reason}); the token was discarded.")]
    private partial void LogNotDelivered(string reason);
}
