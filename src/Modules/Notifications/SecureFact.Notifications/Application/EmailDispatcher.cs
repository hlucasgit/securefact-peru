using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SecureFact.Notifications.Contracts;
using SecureFact.Notifications.Infrastructure;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Messaging;

namespace SecureFact.Notifications.Application;

/// <param name="Sent">E-mails that left.</param>
/// <param name="Failed">E-mails whose delivery failed; they will be tried again.</param>
/// <param name="Dead">Failed e-mails that reached the attempt limit and wait for an operator.</param>
public sealed record EmailDispatchReport(int Sent, int Failed, int Dead)
{
    public static EmailDispatchReport Empty { get; } = new(0, 0, 0);
}

/// <summary>An e-mail that failed all its attempts and waits for an operator (platform staff). The text of the message is still kept so that it can be sent again.</summary>
public sealed record DeadEmail(Guid Id, string ToAddress, string Subject, int Attempts, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset DeadAt);

/// <summary>Sends the queued e-mails (ADR-054). The background worker calls it; a test can call it to make the queue run.</summary>
public interface IEmailDispatcher
{
    Task<EmailDispatchReport> RunOnceAsync(CancellationToken cancellationToken);

    /// <summary>The e-mails that died, oldest first. Platform scope.</summary>
    Task<IReadOnlyList<DeadEmail>> ListDeadAsync(int max, CancellationToken cancellationToken);

    /// <summary>Puts a dead e-mail back in the queue with its attempts reset. False when it is not dead.</summary>
    Task<bool> RequeueAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Removes the e-mails that were sent, or that died, more than <paramref name="retention"/> ago. Returns how many.</summary>
    Task<int> PurgeAsync(TimeSpan retention, CancellationToken cancellationToken);
}

/// <summary>
/// At-least-once, like the outbox of the modules (ADR-022): an e-mail is marked sent after the channel took it, so a crash in between sends it again; the retries use the same backoff and
/// the same limit as the outbox (<see cref="OutboxPolicy"/>). Several workers share the queue (SKIP LOCKED); a lease releases what a dead one held.
/// </summary>
internal sealed partial class EmailDispatcher(NotificationsDbContext db, DataScope scope, IEmailSender sender, TimeProvider clock, ILogger<EmailDispatcher> logger) : IEmailDispatcher
{
    private const int BatchSize = 20;

    private sealed record Claimed(Guid Id, string ToAddress, string Subject, string TextBody, string HtmlBody, string? FromName, string? ReplyTo, int Attempts);

    public async Task<EmailDispatchReport> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var elevated = scope.Elevate("notifications: send the queued e-mails");
        var now = clock.GetUtcNow();
        var leaseUntil = now + OutboxPolicy.Lease;
        var claimed = await db.Database.SqlQuery<Claimed>($"""
            UPDATE notifications.email_queue m
            SET locked_until = {leaseUntil}, attempts = m.attempts + 1
            WHERE m.id IN (
                SELECT id FROM notifications.email_queue
                WHERE sent_at IS NULL AND dead_at IS NULL AND next_attempt_at <= {now}
                  AND (locked_until IS NULL OR locked_until < {now})
                ORDER BY created_at
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED)
            RETURNING m.id AS "Id", m.to_address AS "ToAddress", m.subject AS "Subject", m.text_body AS "TextBody", m.html_body AS "HtmlBody",
                      m.from_name AS "FromName", m.reply_to AS "ReplyTo", m.attempts AS "Attempts"
            """).ToListAsync(cancellationToken);

        var report = EmailDispatchReport.Empty;
        foreach (var email in claimed.OrderBy(e => e.Attempts))
        {
            try
            {
                await sender.SendAsync(new EmailMessage(email.ToAddress, email.Subject, email.TextBody, email.HtmlBody, email.FromName, email.ReplyTo), cancellationToken);
            }
            catch (EmailDeliveryException failure)
            {
                // The text of the failure names the type only (see the channels): the queue and the log never hold the body of the message.
                var dead = email.Attempts >= OutboxPolicy.MaxAttempts;
                await FailAsync(email.Id, failure.Message, cancellationToken);
                LogFailed(logger, email.Id, email.Attempts, dead);
                report = report with { Failed = report.Failed + 1, Dead = report.Dead + (dead ? 1 : 0) };
                continue;
            }

            await db.Database.ExecuteSqlAsync($"""
                UPDATE notifications.email_queue
                SET sent_at = {clock.GetUtcNow()}, locked_until = NULL, last_error = NULL, text_body = '', html_body = ''
                WHERE id = {email.Id} AND sent_at IS NULL
                """, CancellationToken.None);
            report = report with { Sent = report.Sent + 1 };
        }

        return report;
    }

    public async Task<IReadOnlyList<DeadEmail>> ListDeadAsync(int max, CancellationToken cancellationToken)
    {
        using var elevated = scope.Elevate("notifications: list the dead e-mails");
        return await db.Database.SqlQuery<DeadEmail>($"""
            SELECT id AS "Id", to_address AS "ToAddress", subject AS "Subject", attempts AS "Attempts", last_error AS "LastError", created_at AS "CreatedAt", dead_at AS "DeadAt"
            FROM notifications.email_queue
            WHERE dead_at IS NOT NULL AND sent_at IS NULL
            ORDER BY created_at
            LIMIT {Math.Clamp(max, 1, 200)}
            """).ToListAsync(cancellationToken);
    }

    public async Task<bool> RequeueAsync(Guid id, CancellationToken cancellationToken)
    {
        using var elevated = scope.Elevate("notifications: send a dead e-mail again");
        var changed = await db.Database.ExecuteSqlAsync($"""
            UPDATE notifications.email_queue
            SET dead_at = NULL, attempts = 0, next_attempt_at = {clock.GetUtcNow()}, locked_until = NULL
            WHERE id = {id} AND dead_at IS NOT NULL AND sent_at IS NULL
            """, cancellationToken);
        return changed == 1;
    }

    public async Task<int> PurgeAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        using var elevated = scope.Elevate("notifications: purge the queue of e-mails");
        var before = clock.GetUtcNow() - retention;
        return await db.Database.ExecuteSqlAsync($"""
            DELETE FROM notifications.email_queue
            WHERE (sent_at IS NOT NULL AND sent_at < {before}) OR (dead_at IS NOT NULL AND dead_at < {before})
            """, cancellationToken);
    }

    private async Task FailAsync(Guid id, string error, CancellationToken cancellationToken)
    {
        var clipped = error.Length > 500 ? error[..500] : error;
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlAsync($"""
            UPDATE notifications.email_queue
            SET last_error = {clipped},
                locked_until = NULL,
                next_attempt_at = {now} + make_interval(secs => LEAST({OutboxPolicy.BaseBackoff.TotalSeconds} * power(2, GREATEST(attempts - 1, 0)), {OutboxPolicy.MaxBackoff.TotalSeconds})),
                dead_at = CASE WHEN attempts >= {OutboxPolicy.MaxAttempts} THEN {now} ELSE NULL END
            WHERE id = {id} AND sent_at IS NULL
            """, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "E-mail {Id} was not delivered (attempt {Attempt}, dead: {Dead}).")]
    private static partial void LogFailed(ILogger logger, Guid id, int attempt, bool dead);
}
