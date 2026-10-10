using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Audit.Domain;
using SecureFact.Audit.Infrastructure;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.SharedKernel.Telemetry;

namespace SecureFact.Audit.Application;

internal sealed class AuditTrail(
    AuditDbContext db,
    DataScope scope,
    ICurrentUser currentUser,
    IRequestContext request,
    TimeProvider clock) : IAuditTrail
{
    private static readonly byte[] GenesisHash = new byte[32];

    public async Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        // Appending is a trusted, narrow internal operation: it must work for any caller scope but can only ever insert.
        using var elevated = scope.Elevate("audit:append");

        var chainKey = AuditEventEntity.ChainKeyFor(auditEvent.TenantId);
        var actorId = auditEvent.ActorUserId ?? currentUser.UserId;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Serialises writers of one chain so sequence numbers and hashes link without gaps or forks.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chainKey}, 0))", cancellationToken);

        var last = await db.Events.AsNoTracking()
            .Where(e => e.ChainKey == chainKey)
            .OrderByDescending(e => e.Sequence)
            .Select(e => new { e.Sequence, e.Hash })
            .FirstOrDefaultAsync(cancellationToken);

        var entity = AuditEventEntity.Create(
            Guid.CreateVersion7(),
            auditEvent.TenantId,
            (last?.Sequence ?? 0) + 1,
            last?.Hash ?? GenesisHash,
            clock.GetUtcNow(),
            actorId is null ? "system" : currentUser.IsSupportAccess ? "support" : "user",
            actorId,
            auditEvent.Action,
            auditEvent.EntityType,
            auditEvent.EntityId,
            AuditScrubber.ToCanonicalJson(auditEvent.OldValues),
            AuditScrubber.ToCanonicalJson(auditEvent.NewValues),
            request.IpAddress,
            request.UserAgent,
            request.CorrelationId,
            request.RequestId);

        db.Events.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        SecureFactTelemetry.AuditEvents.Add(1, new KeyValuePair<string, object?>("action", auditEvent.Action));
    }
}
