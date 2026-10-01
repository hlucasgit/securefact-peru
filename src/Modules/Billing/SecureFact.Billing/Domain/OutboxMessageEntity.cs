using SecureFact.Platform.Persistence;

namespace SecureFact.Billing.Domain;

/// <summary>An integration event stored in the transaction that caused it. Immutable except for its delivery bookkeeping (database trigger).</summary>
internal sealed class OutboxMessageEntity : ITenantOwned
{
    private OutboxMessageEntity()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public string PayloadJson { get; private set; } = "{}";

    public DateTimeOffset CreatedAt { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public DateTimeOffset? DeadAt { get; private set; }

    public string? LastError { get; private set; }

    public static OutboxMessageEntity Create(Guid tenantId, string eventType, string payloadJson, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        EventType = eventType,
        PayloadJson = payloadJson,
        CreatedAt = now,
        NextAttemptAt = now,
    };
}
