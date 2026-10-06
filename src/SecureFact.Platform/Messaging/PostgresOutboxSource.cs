using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SecureFact.SharedKernel.Messaging;

namespace SecureFact.Platform.Messaging;

/// <summary>
/// The outbox of a module whose table is <c>{schema}.outbox_message</c> (ADR-022, ADR-035): claiming, completing, failing with backoff, listing the dead ones, requeueing and purging, all as
/// SQL on the module's own connection. Claiming spans tenants (platform scope); every other member runs in the scope of the message's tenant, so Row Level Security still protects the data.
/// </summary>
public sealed partial class PostgresOutboxSource : IOutboxSource
{
    private readonly DbContext _db;
    private readonly TimeProvider _clock;
    private readonly string _table;

    private sealed record Claimed(Guid Id, Guid TenantId, string EventType, string Payload, int Attempts, DateTimeOffset CreatedAt);

    private sealed record DeadRow(Guid Id, string EventType, int Attempts, string? LastError, DateTimeOffset CreatedAt);

    /// <param name="db">Context of the module that owns the table (the connection already carries the RLS scope).</param>
    /// <param name="schema">Schema of the module; a plain lower-case identifier, because it is part of the SQL text.</param>
    /// <param name="name">Name of the source in the operator endpoints and in the dispatcher.</param>
    public PostgresOutboxSource(DbContext db, TimeProvider clock, string schema, string name)
    {
        if (!Identifier().IsMatch(schema))
        {
            throw new ArgumentException("The schema must be a lower-case identifier.", nameof(schema));
        }

        _db = db;
        _clock = clock;
        _table = $"{schema}.outbox_message";
        Name = name;
    }

    public string Name { get; }

    [GeneratedRegex("^[a-z_][a-z0-9_]*$")]
    private static partial Regex Identifier();

    /// <summary>The statement with the table in place of <c>@table</c> and the arguments as parameters <c>{0}</c>, <c>{1}</c>...</summary>
    private FormattableString Sql(string template, params object?[] arguments) =>
        FormattableStringFactory.Create(template.Replace("@table", _table, StringComparison.Ordinal), arguments);

    public async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(int max, Guid? onlyTenant, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var leaseUntil = now + OutboxPolicy.Lease;

        // SKIP LOCKED lets several dispatchers share the queue without waiting for each other; the lease releases messages of a dead one.
        var rows = await _db.Database.SqlQuery<Claimed>(Sql("""
            UPDATE @table m
            SET locked_until = {0}, attempts = m.attempts + 1
            WHERE m.id IN (
                SELECT id FROM @table
                WHERE processed_at IS NULL AND dead_at IS NULL AND next_attempt_at <= {1}
                  AND (locked_until IS NULL OR locked_until < {1})
                  AND ({2}::uuid IS NULL OR tenant_id = {2}::uuid)
                ORDER BY created_at
                LIMIT {3}
                FOR UPDATE SKIP LOCKED)
            RETURNING m.id AS "Id", m.tenant_id AS "TenantId", m.event_type AS "EventType", m.payload::text AS "Payload",
                      m.attempts AS "Attempts", m.created_at AS "CreatedAt"
            """, leaseUntil, now, onlyTenant, max)).ToListAsync(cancellationToken);

        return rows.OrderBy(r => r.CreatedAt).Select(r => new OutboxMessage(r.Id, r.TenantId, r.EventType, r.Payload, r.Attempts, r.CreatedAt)).ToList();
    }

    public async Task CompleteAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await _db.Database.ExecuteSqlAsync(Sql("""
            UPDATE @table SET processed_at = {0}, locked_until = NULL, last_error = NULL
            WHERE id = {1} AND processed_at IS NULL
            """, _clock.GetUtcNow(), messageId), cancellationToken);
    }

    public async Task FailAsync(Guid messageId, string error, CancellationToken cancellationToken)
    {
        // Backoff doubles with every attempt (same formula as OutboxPolicy.Backoff); the message dies at the attempt limit.
        await _db.Database.ExecuteSqlAsync(Sql("""
            UPDATE @table
            SET last_error = {0},
                locked_until = NULL,
                next_attempt_at = {1} + make_interval(secs => LEAST({2} * power(2, GREATEST(attempts - 1, 0)), {3})),
                dead_at = CASE WHEN attempts >= {4} THEN {1} ELSE NULL END
            WHERE id = {5} AND processed_at IS NULL
            """, error, _clock.GetUtcNow(), OutboxPolicy.BaseBackoff.TotalSeconds, OutboxPolicy.MaxBackoff.TotalSeconds, OutboxPolicy.MaxAttempts, messageId), cancellationToken);
    }

    public async Task<IReadOnlyList<DeadOutboxMessage>> ListDeadAsync(int max, CancellationToken cancellationToken)
    {
        var rows = await _db.Database.SqlQuery<DeadRow>(Sql("""
            SELECT id AS "Id", event_type AS "EventType", attempts AS "Attempts", last_error AS "LastError", created_at AS "CreatedAt"
            FROM @table WHERE dead_at IS NOT NULL AND processed_at IS NULL
            ORDER BY created_at LIMIT {0}
            """, Math.Clamp(max, 1, 200))).ToListAsync(cancellationToken);
        return rows.Select(r => new DeadOutboxMessage(r.Id, Name, r.EventType, r.Attempts, r.LastError, r.CreatedAt)).ToList();
    }

    public async Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var changed = await _db.Database.ExecuteSqlAsync(Sql("""
            UPDATE @table SET dead_at = NULL, attempts = 0, next_attempt_at = {0}, locked_until = NULL
            WHERE id = {1} AND dead_at IS NOT NULL AND processed_at IS NULL
            """, _clock.GetUtcNow(), messageId), cancellationToken);
        return changed == 1;
    }

    public async Task<int> PurgeDeliveredAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        // The delete goes through a function that only removes delivered messages older than the retention: the table itself stays append-only for everybody (see OutboxSql).
        var schema = _table[..^".outbox_message".Length];
        var removed = await _db.Database.SqlQuery<int>(FormattableStringFactory.Create($"SELECT {schema}.purge_outbox_messages(make_interval(secs => {{0}})) AS \"Value\"", retention.TotalSeconds))
            .ToListAsync(cancellationToken);
        return removed.Single();
    }
}
