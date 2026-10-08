namespace SecureFact.Notifications.Domain;

/// <summary>
/// An e-mail waiting to be sent (ADR-054). It holds the whole message until it is sent; once it is, the text goes (only the address, the subject and the dates stay, for support), and a message
/// that fails ten times is left dead for an operator. Never a one-time token: those are sent at once and not stored (ADR-052).
/// </summary>
internal sealed class QueuedEmail
{
    private QueuedEmail()
    {
    }

    public Guid Id { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public string ToAddress { get; private set; } = string.Empty;

    public string Subject { get; private set; } = string.Empty;

    public string TextBody { get; private set; } = string.Empty;

    public string HtmlBody { get; private set; } = string.Empty;

    public string? FromName { get; private set; }

    public string? ReplyTo { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public DateTimeOffset? DeadAt { get; private set; }

    public string? LastError { get; private set; }

    public static QueuedEmail Create(Contracts.EmailMessage message, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        CreatedAt = now,
        ToAddress = message.To,
        Subject = message.Subject,
        TextBody = message.Text,
        HtmlBody = message.Html,
        FromName = message.FromName,
        ReplyTo = message.ReplyTo,
        NextAttemptAt = now,
    };
}
