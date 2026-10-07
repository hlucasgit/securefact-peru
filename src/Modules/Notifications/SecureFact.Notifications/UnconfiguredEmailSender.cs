using SecureFact.Notifications.Contracts;

namespace SecureFact.Notifications;

/// <summary>No channel is configured: nothing leaves, and the caller is told so. It is the safe default.</summary>
internal sealed class UnconfiguredEmailSender : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
        throw new EmailDeliveryException("No e-mail channel is configured (Email:Provider).");
}
