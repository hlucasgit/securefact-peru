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
    /// <param name="message">What to send.</param>
    /// <param name="dedupeKey">
    /// Names the fact that the message tells (for example «this certificate, 15 days left»). A second message with the same key to the same address is not queued, ever, so a notice that
    /// is raised again does not reach the person twice. Null: no such guarantee.
    /// </param>
    /// <returns>False when no e-mail channel is configured, or the same fact was already told to that address; true when it was queued.</returns>
    Task<bool> EnqueueAsync(EmailMessage message, string? dedupeKey = null, CancellationToken cancellationToken = default);
}

/// <summary>The plan of an account reached a share of its monthly allowance of documents (ADR-055). <paramref name="Percent"/> is 80 or 100.</summary>
public sealed record PlanUsageNotice(Guid TenantId, string PlanName, int Used, int Limit, int Percent, string Period);

/// <summary>SUNAT rejected a document, or it could not be sent for good (ADR-055). The description is the one that SUNAT gave.</summary>
public sealed record DocumentRejectedNotice(Guid TenantId, Guid ElectronicDocumentId, string DocumentName, bool Rejected, int? ResponseCode, string? Description);

/// <summary>Tells the owners of an account about its business: the use of its plan and the fate of its documents. A failed notice never undoes what it announces.</summary>
public interface IBusinessNotices
{
    Task PlanUsageAsync(PlanUsageNotice notice, CancellationToken cancellationToken);

    Task DocumentRejectedAsync(DocumentRejectedNotice notice, CancellationToken cancellationToken);
}

/// <summary>The default when nobody listens (a host without the notices): the work goes on and nothing is sent.</summary>
public sealed class NullBusinessNotices : IBusinessNotices
{
    public Task PlanUsageAsync(PlanUsageNotice notice, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DocumentRejectedAsync(DocumentRejectedNotice notice, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Sends an e-mail through the channel that the operator configured. Throws <see cref="EmailDeliveryException"/> when it cannot.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
