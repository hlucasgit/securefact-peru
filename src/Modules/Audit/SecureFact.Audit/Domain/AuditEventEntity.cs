using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SecureFact.Platform.Persistence;

namespace SecureFact.Audit.Domain;

/// <summary>Immutable audit row. There is no way to change one after creation: the database refuses UPDATE and DELETE.</summary>
internal sealed class AuditEventEntity : IOptionalTenantOwned
{
    public const string PlatformChain = "platform";

    private AuditEventEntity()
    {
    }

    public Guid Id { get; private set; }

    public Guid? TenantId { get; private set; }

    public string ChainKey { get; private set; } = string.Empty;

    public long Sequence { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public string ActorType { get; private set; } = string.Empty;

    public Guid? ActorUserId { get; private set; }

    public string Action { get; private set; } = string.Empty;

    public string EntityType { get; private set; } = string.Empty;

    public string? EntityId { get; private set; }

    /// <summary>Canonical JSON text. Kept as text (not jsonb) so the exact bytes that were hashed can be re-read.</summary>
    public string? OldValues { get; private set; }

    public string? NewValues { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public string? CorrelationId { get; private set; }

    public string? RequestId { get; private set; }

    public byte[] PreviousHash { get; private set; } = [];

    public byte[] Hash { get; private set; } = [];

    public static string ChainKeyFor(Guid? tenantId) => tenantId?.ToString("D") ?? PlatformChain;

    /// <summary>PostgreSQL timestamptz keeps microseconds; truncating up front keeps the stored and hashed value identical.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset value) => new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    public static AuditEventEntity Create(
        Guid id, Guid? tenantId, long sequence, byte[] previousHash, DateTimeOffset occurredAt, string actorType, Guid? actorUserId,
        string action, string entityType, string? entityId, string? oldValues, string? newValues,
        string? ip, string? userAgent, string? correlationId, string? requestId)
    {
        var entity = new AuditEventEntity
        {
            Id = id,
            TenantId = tenantId,
            ChainKey = ChainKeyFor(tenantId),
            Sequence = sequence,
            OccurredAt = Truncate(occurredAt),
            ActorType = actorType,
            ActorUserId = actorUserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            OldValues = oldValues,
            NewValues = newValues,
            IpAddress = ip,
            UserAgent = userAgent,
            CorrelationId = correlationId,
            RequestId = requestId,
            PreviousHash = previousHash,
        };
        entity.Hash = entity.ComputeHash();
        return entity;
    }

    /// <summary>SHA-256 over a JSON array of every field plus the previous hash, so any edit, deletion or reordering breaks the chain.</summary>
    public byte[] ComputeHash()
    {
        var fields = new object?[]
        {
            Id, ChainKey, Sequence, OccurredAt.UtcTicks, ActorType, ActorUserId, Action, EntityType, EntityId,
            OldValues, NewValues, IpAddress, UserAgent, CorrelationId, RequestId, Convert.ToHexString(PreviousHash),
        };
        return SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields)));
    }
}
