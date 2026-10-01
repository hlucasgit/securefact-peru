namespace SecureFact.SharedKernel.Messaging;

/// <summary>An integration event written in the same transaction as the business change that caused it (transactional outbox).</summary>
public sealed record OutboxMessage(Guid Id, Guid TenantId, string EventType, string PayloadJson, int Attempts, DateTimeOffset CreatedAt);

/// <summary>A message that exhausted its delivery attempts and waits for an operator.</summary>
public sealed record DeadOutboxMessage(Guid Id, string Source, string EventType, int Attempts, string? LastError, DateTimeOffset CreatedAt);

/// <summary>Delivery policy shared by every outbox: at-least-once, exponential backoff, dead after a fixed number of attempts.</summary>
public static class OutboxPolicy
{
    public const int MaxAttempts = 10;

    /// <summary>How long a claimed message is invisible to other dispatchers; a dispatcher that dies releases it by expiry.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(BaseBackoff.TotalSeconds * Math.Pow(2, Math.Max(attempts - 1, 0)), MaxBackoff.TotalSeconds));
}

/// <summary>
/// The outbox of one module. <see cref="ClaimAsync"/> runs in platform scope (it spans tenants); every other member runs in the
/// scope of the message's tenant, so Row Level Security still protects the data.
/// </summary>
public interface IOutboxSource
{
    string Name { get; }

    /// <summary>Leases up to <paramref name="max"/> due messages (oldest first), counting one more attempt for each.</summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimAsync(int max, Guid? onlyTenant, CancellationToken cancellationToken);

    Task CompleteAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>Records the failure and schedules the next attempt, or marks the message dead once <see cref="OutboxPolicy.MaxAttempts"/> is reached.</summary>
    Task FailAsync(Guid messageId, string error, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeadOutboxMessage>> ListDeadAsync(int max, CancellationToken cancellationToken);

    /// <summary>Puts a dead message back in the queue with its attempts reset. False when the message is not dead (or not visible).</summary>
    Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken);
}

/// <summary>
/// Consumer of one event type. Delivery is at-least-once, so handlers must be idempotent; a thrown exception means "not handled" and the
/// message is retried later. A handler runs in the scope of the message's tenant.
/// </summary>
public interface IIntegrationEventConsumer
{
    string EventType { get; }

    Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken);
}

/// <param name="Delivered">Messages handled and completed.</param>
/// <param name="Failed">Messages whose handler failed (or that had none); they will be retried.</param>
/// <param name="Dead">Failed messages that reached the attempt limit.</param>
public sealed record OutboxReport(int Delivered, int Failed, int Dead)
{
    public static OutboxReport Empty { get; } = new(0, 0, 0);
}

public interface IOutboxProcessor
{
    /// <param name="onlyTenant">Limits the pass to one tenant (a support run); null covers every tenant, as the background worker does.</param>
    Task<OutboxReport> RunOnceAsync(CancellationToken cancellationToken, Guid? onlyTenant = null);
}
