using Microsoft.EntityFrameworkCore;
using SecureFact.Billing.Infrastructure;
using SecureFact.SharedKernel.Messaging;

namespace SecureFact.Billing.Application;

/// <summary>Outbox of the Billing module: <c>billing.outbox_message</c>. Claiming spans tenants (platform scope); everything else is per tenant.</summary>
internal sealed class BillingOutboxSource(BillingDbContext db, TimeProvider clock) : IOutboxSource
{
    public const string SourceName = "billing";

    private sealed record Claimed(Guid Id, Guid TenantId, string EventType, string Payload, int Attempts, DateTimeOffset CreatedAt);

    public string Name => SourceName;

    public async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(int max, Guid? onlyTenant, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var leaseUntil = now + OutboxPolicy.Lease;

        // SKIP LOCKED lets several dispatchers share the queue without waiting for each other; the lease releases messages of a dead one.
        var rows = await db.Database.SqlQuery<Claimed>($"""
            UPDATE billing.outbox_message m
            SET locked_until = {leaseUntil}, attempts = m.attempts + 1
            WHERE m.id IN (
                SELECT id FROM billing.outbox_message
                WHERE processed_at IS NULL AND dead_at IS NULL AND next_attempt_at <= {now}
                  AND (locked_until IS NULL OR locked_until < {now})
                  AND ({onlyTenant}::uuid IS NULL OR tenant_id = {onlyTenant}::uuid)
                ORDER BY created_at
                LIMIT {max}
                FOR UPDATE SKIP LOCKED)
            RETURNING m.id AS "Id", m.tenant_id AS "TenantId", m.event_type AS "EventType", m.payload::text AS "Payload",
                      m.attempts AS "Attempts", m.created_at AS "CreatedAt"
            """).ToListAsync(cancellationToken);

        return rows.OrderBy(r => r.CreatedAt).Select(r => new OutboxMessage(r.Id, r.TenantId, r.EventType, r.Payload, r.Attempts, r.CreatedAt)).ToList();
    }

    public async Task CompleteAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE billing.outbox_message SET processed_at = {now}, locked_until = NULL, last_error = NULL
            WHERE id = {messageId} AND processed_at IS NULL
            """, cancellationToken);
    }

    public async Task FailAsync(Guid messageId, string error, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var maxAttempts = OutboxPolicy.MaxAttempts;
        var baseSeconds = OutboxPolicy.BaseBackoff.TotalSeconds;
        var capSeconds = OutboxPolicy.MaxBackoff.TotalSeconds;

        // Backoff doubles with every attempt (same formula as OutboxPolicy.Backoff); the message dies at the attempt limit.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE billing.outbox_message
            SET last_error = {error},
                locked_until = NULL,
                next_attempt_at = {now} + make_interval(secs => LEAST({baseSeconds} * power(2, GREATEST(attempts - 1, 0)), {capSeconds})),
                dead_at = CASE WHEN attempts >= {maxAttempts} THEN {now} ELSE NULL END
            WHERE id = {messageId} AND processed_at IS NULL
            """, cancellationToken);
    }

    public async Task<IReadOnlyList<DeadOutboxMessage>> ListDeadAsync(int max, CancellationToken cancellationToken)
    {
        var rows = await db.OutboxMessages.AsNoTracking().Where(m => m.DeadAt != null && m.ProcessedAt == null)
            .OrderBy(m => m.CreatedAt).Take(Math.Clamp(max, 1, 200)).ToListAsync(cancellationToken);
        return rows.Select(m => new DeadOutboxMessage(m.Id, Name, m.EventType, m.Attempts, m.LastError, m.CreatedAt)).ToList();
    }

    public async Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE billing.outbox_message SET dead_at = NULL, attempts = 0, next_attempt_at = {now}, locked_until = NULL
            WHERE id = {messageId} AND dead_at IS NOT NULL AND processed_at IS NULL
            """, cancellationToken);
        return changed == 1;
    }
}
