using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Audit.Domain;
using SecureFact.Audit.Infrastructure;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Audit.Application;

internal sealed class AuditQuery(AuditDbContext db, IDataScope scope) : IAuditQuery
{
    private const int MaxPage = 200;
    private const int VerifyBatch = 500;
    private static readonly byte[] GenesisHash = new byte[32];

    public async Task<IReadOnlyList<AuditRecord>> ListAsync(
        Guid? tenantId, string? action, string? entityType, string? entityId, int skip, int take, CancellationToken cancellationToken)
    {
        var query = db.Events.AsNoTracking().AsQueryable();

        // Tenants are limited by RLS to their own chain; only platform scope may choose another one.
        if (scope.Kind == DataScopeKind.Platform)
        {
            query = tenantId is { } t ? query.Where(e => e.TenantId == t) : query.Where(e => e.TenantId == null);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(e => e.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query = query.Where(e => e.EntityType == entityType);
        }

        if (!string.IsNullOrWhiteSpace(entityId))
        {
            query = query.Where(e => e.EntityId == entityId);
        }

        var rows = await query
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Sequence)
            .Skip(Math.Max(skip, 0)).Take(Math.Clamp(take, 1, MaxPage))
            .ToListAsync(cancellationToken);

        return rows.Select(e => new AuditRecord(
            e.Id, e.TenantId, e.Sequence, e.OccurredAt, e.ActorType, e.ActorUserId, e.Action, e.EntityType, e.EntityId,
            e.OldValues, e.NewValues, e.IpAddress, e.UserAgent, e.CorrelationId, e.RequestId)).ToList();
    }

    public async Task<AuditVerification> VerifyAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        var target = scope.Kind == DataScopeKind.Platform ? tenantId : scope.Current?.Value;
        var chainKey = AuditEventEntity.ChainKeyFor(target);

        long checkedCount = 0;
        long expectedSequence = 1;
        var previousHash = GenesisHash;

        while (true)
        {
            var batch = await db.Events.AsNoTracking()
                .Where(e => e.ChainKey == chainKey && e.Sequence >= expectedSequence)
                .OrderBy(e => e.Sequence)
                .Take(VerifyBatch)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                return new AuditVerification(true, checkedCount, null, null);
            }

            foreach (var entry in batch)
            {
                if (entry.Sequence != expectedSequence)
                {
                    return Broken(checkedCount, expectedSequence, "A sequence number is missing: an event was deleted.");
                }

                if (!CryptographicOperations.FixedTimeEquals(entry.PreviousHash, previousHash))
                {
                    return Broken(checkedCount, entry.Sequence, "The link to the previous event does not match.");
                }

                if (!CryptographicOperations.FixedTimeEquals(entry.Hash, entry.ComputeHash()))
                {
                    return Broken(checkedCount, entry.Sequence, "The event content does not match its hash: it was modified.");
                }

                previousHash = entry.Hash;
                expectedSequence++;
                checkedCount++;
            }
        }
    }

    private static AuditVerification Broken(long checkedCount, long sequence, string reason) =>
        new(false, checkedCount, sequence, reason);
}
