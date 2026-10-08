using System.Globalization;
using SecureFact.Identity.Contracts;
using SecureFact.Notifications;
using SecureFact.Notifications.Contracts;

namespace SecureFact.Api.Security;

/// <summary>
/// Delivers the link to choose a new password by e-mail, signed with the brand of the reseller of the account and pointing to its portal when it has a verified domain (ADR-052). It
/// sends at once and does not use the queue: the message holds a one-time token, which is never stored. A failed delivery is logged without the token and is not reported to the caller:
/// the answer of the request must be the same whether the address exists or not.
/// </summary>
internal sealed partial class EmailPasswordResetNotifier(
    IEmailSender email,
    NoticeContext context,
    TimeProvider clock,
    ILogger<EmailPasswordResetNotifier> logger) : IPasswordResetNotifier
{
    private static readonly TimeZoneInfo Lima = TimeZoneInfo.FindSystemTimeZoneById("America/Lima");

    public async Task SendAsync(PasswordResetDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var (brand, baseUrl, _) = await context.ForAsync(delivery.TenantId, delivery.ResellerId, cancellationToken);
        var link = string.Create(CultureInfo.InvariantCulture, $"{baseUrl}/restablecer#token={Uri.EscapeDataString(delivery.Token)}");
        var minutes = Math.Max(1, (int)Math.Ceiling((delivery.ExpiresAt - clock.GetUtcNow()).TotalMinutes));
        await DeliverAsync(PasswordResetEmail.Compose(delivery.Email, brand, link, minutes), cancellationToken);
    }

    public async Task NotifyChangedAsync(PasswordChangedNotice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var (brand, baseUrl, _) = await context.ForAsync(notice.TenantId, notice.ResellerId, cancellationToken);
        var when = TimeZoneInfo.ConvertTime(notice.ChangedAt, Lima).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        await DeliverAsync(PasswordChangedEmail.Compose(notice.Email, brand, $"{baseUrl}/recuperar", when), cancellationToken);
    }

    private async Task DeliverAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await email.SendAsync(message, cancellationToken);
        }
        catch (EmailDeliveryException failure)
        {
            LogNotDelivered(failure.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A password reset e-mail was not delivered ({Reason}); the token was discarded.")]
    private partial void LogNotDelivered(string reason);
}
