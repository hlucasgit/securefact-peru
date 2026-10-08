using SecureFact.Notifications.Contracts;
using SecureFact.Notifications.Domain;
using SecureFact.Notifications.Infrastructure;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Notifications.Application;

internal sealed class EmailOutbox(NotificationsDbContext db, DataScope scope, EmailOptions options, TimeProvider clock) : IEmailOutbox
{
    public async Task<bool> EnqueueAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // With no channel the message could never leave: keeping it would only fill the queue with failures. The operator who sets a channel later does not get the old notices.
        if (options.Provider == EmailOptions.ProviderNone)
        {
            return false;
        }

        using var elevated = scope.Elevate("notifications: queue an e-mail");
        db.Emails.Add(QueuedEmail.Create(message, clock.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
