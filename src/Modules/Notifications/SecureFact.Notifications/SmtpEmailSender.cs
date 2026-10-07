using MailKit.Net.Smtp;
using MailKit.Security;
using SecureFact.Notifications.Contracts;

namespace SecureFact.Notifications;

internal sealed class SmtpEmailSender(EmailOptions options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var mime = EmailMime.Build(message, options);
        var smtp = options.Smtp;
        try
        {
            using var client = new SmtpClient { Timeout = smtp.TimeoutSeconds * 1000 };
            await client.ConnectAsync(smtp.Host ?? throw new EmailDeliveryException("Email:Smtp:Host is not configured."), smtp.Port, SecurityOf(smtp.Security), cancellationToken);
            if (!string.IsNullOrEmpty(smtp.User))
            {
                await client.AuthenticateAsync(smtp.User, smtp.Password ?? string.Empty, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Only the type of the failure travels: the text of an SMTP error can echo the recipient or the credentials.
            throw new EmailDeliveryException($"The SMTP server did not accept the e-mail ({failure.GetType().Name}).", failure);
        }
    }

    internal static SecureSocketOptions SecurityOf(string security) => security switch
    {
        "Tls" => SecureSocketOptions.SslOnConnect,
        "None" => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls,
    };
}
