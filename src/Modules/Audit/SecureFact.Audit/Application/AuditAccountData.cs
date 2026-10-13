using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.SharedKernel.Export;

namespace SecureFact.Audit.Application;

/// <summary>The audit trail of the account with the hashes of its chain, so the account can verify it apart from the platform (ADR-074).</summary>
internal sealed class AuditAccountData(AuditDbContext db) : IAccountDataContributor
{
    public Task WriteAsync(IAccountDataWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return writer.WriteTableAsync(
            "auditoria",
            ["secuencia", "fecha", "tipo_de_actor", "usuario_id", "accion", "entidad", "entidad_id", "valores_anteriores", "valores_nuevos", "ip", "agente", "correlacion", "hash_anterior", "hash"],
            db.Events.AsNoTracking().OrderBy(e => e.ChainKey).ThenBy(e => e.Sequence).InBatchesAsync(500, cancellationToken)
                .Select(e => (IReadOnlyList<string?>)[AccountCsv.Number(e.Sequence), AccountCsv.Stamp(e.OccurredAt), e.ActorType, e.ActorUserId?.ToString("D"), e.Action, e.EntityType, e.EntityId, e.OldValues, e.NewValues, e.IpAddress, e.UserAgent, e.CorrelationId, Convert.ToHexStringLower(e.PreviousHash), Convert.ToHexStringLower(e.Hash)]),
            cancellationToken);
    }
}
