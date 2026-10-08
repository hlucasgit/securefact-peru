using Microsoft.EntityFrameworkCore;
using SecureFact.Notifications.Contracts;
using SecureFact.Notifications.Infrastructure;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Notifications.Application;

internal sealed class EmailOutbox(NotificationsDbContext db, DataScope scope, EmailOptions options, TimeProvider clock) : IEmailOutbox
{
    public async Task<bool> EnqueueAsync(EmailMessage message, string? dedupeKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // With no channel the message could never leave: keeping it would only fill the queue with failures. The operator who sets a channel later does not get the old notices.
        if (options.Provider == EmailOptions.ProviderNone)
        {
            return false;
        }

        // The fact is told once per address. The unique index decides, not a read before the write, so two workers cannot both queue it.
        var key = dedupeKey is null ? null : $"{dedupeKey}|{message.To.Trim().ToLowerInvariant()}";
        var now = clock.GetUtcNow();
        using var elevated = scope.Elevate("notifications: queue an e-mail");
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO notifications.email_queue
                (id, created_at, to_address, subject, text_body, html_body, from_name, reply_to, attempts, next_attempt_at, dedupe_key)
            VALUES ({Guid.CreateVersion7()}, {now}, {message.To}, {message.Subject}, {message.Text}, {message.Html}, {message.FromName}, {message.ReplyTo}, 0, {now}, {key})
            ON CONFLICT (dedupe_key) WHERE dedupe_key IS NOT NULL DO NOTHING
            """, cancellationToken);
        return inserted == 1;
    }
}
