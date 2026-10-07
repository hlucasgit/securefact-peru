namespace SecureFact.Notifications.Contracts;

/// <summary>
/// An e-mail ready to send: plain text and HTML versions of the same content (ADR-052). It leaves from the platform's address; <paramref name="FromName"/> is only the name shown (the brand
/// of a reseller) and <paramref name="ReplyTo"/> where the answers go (the support address of the reseller).
/// </summary>
public sealed record EmailMessage(string To, string Subject, string Text, string Html, string? FromName = null, string? ReplyTo = null);

/// <summary>The delivery failed or no channel is configured. The message never carries the body of the e-mail: it can hold a one-time token.</summary>
public sealed class EmailDeliveryException : Exception
{
    public EmailDeliveryException()
    {
    }

    public EmailDeliveryException(string message)
        : base(message)
    {
    }

    public EmailDeliveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Sends an e-mail through the channel that the operator configured. Throws <see cref="EmailDeliveryException"/> when it cannot.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
