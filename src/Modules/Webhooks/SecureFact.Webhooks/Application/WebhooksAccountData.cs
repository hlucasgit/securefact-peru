using Microsoft.EntityFrameworkCore;
using SecureFact.Platform.Persistence;
using SecureFact.SharedKernel.Export;
using SecureFact.Webhooks.Infrastructure;

namespace SecureFact.Webhooks.Application;

/// <summary>The destinations of the webhooks of the account (ADR-074). Never their signing secret, and not the deliveries, which are copies of the events.</summary>
internal sealed class WebhooksAccountData(WebhooksDbContext db) : IAccountDataContributor
{
    public Task WriteAsync(IAccountDataWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return writer.WriteTableAsync(
            "webhooks",
            ["id", "url", "descripcion", "eventos", "activo", "desactivado", "motivo_de_desactivacion", "creado"],
            db.Endpoints.AsNoTracking().OrderBy(e => e.Id).InBatchesAsync(500, cancellationToken)
                .Select(e => (IReadOnlyList<string?>)[e.Id.ToString("D"), e.Url, e.Description, string.Join(' ', e.Events), AccountCsv.YesNo(e.IsActive), AccountCsv.Stamp(e.DisabledAt), e.DisabledReason, AccountCsv.Stamp(e.CreatedAt)]),
            cancellationToken);
    }
}
