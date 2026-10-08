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

/// <summary>
/// Leaves an e-mail to be sent later by the background worker, with retries (ADR-054). For notices that carry no secret: what is stored is the whole message. A one-time token never goes
/// through here (it is sent at once and never stored, ADR-052).
/// </summary>
public interface IEmailOutbox
{
    /// <returns>False when no e-mail channel is configured and the message was therefore not kept; true when it was queued.</returns>
    Task<bool> EnqueueAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>Sends an e-mail through the channel that the operator configured. Throws <see cref="EmailDeliveryException"/> when it cannot.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
