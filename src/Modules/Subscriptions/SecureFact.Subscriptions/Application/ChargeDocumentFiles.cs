using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureFact.CpeEngine.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;
using SecureFact.Subscriptions.Contracts;
using SecureFact.Subscriptions.Infrastructure;

namespace SecureFact.Subscriptions.Application;

/// <summary>
/// The PDF and the signed XML of the invoice of a charge. The link to the document is a row of the customer (the row level security shows a tenant only its own), and the file is read in the account
/// of the issuer, which the customer cannot reach by itself.
/// </summary>
internal sealed class ChargeDocumentFiles(SubscriptionsDbContext db, IssuerAccess issuer) : IChargeDocuments
{
    private static readonly Error Missing = Error.NotFound(ErrorCodes.ChargeDocumentNotFound, "Comprobante no encontrado", "El cargo no tiene ese comprobante, o aún no está listo.");

    public Task<Result<byte[]>> GetPdfAsync(Guid chargeId, ChargeDocumentKind kind, CancellationToken cancellationToken) =>
        ReadAsync(chargeId, kind, (electronic, service, token) => service.GetPdfAsync(electronic.Id, token), cancellationToken);

    public Task<Result<string>> GetXmlAsync(Guid chargeId, ChargeDocumentKind kind, CancellationToken cancellationToken) =>
        ReadAsync(chargeId, kind, (electronic, service, token) => service.GetSignedXmlAsync(electronic.Id, token), cancellationToken);

    private async Task<Result<T>> ReadAsync<T>(
        Guid chargeId, ChargeDocumentKind kind, Func<ElectronicDocumentDto, IElectronicDocumentService, CancellationToken, Task<Result<T>>> read, CancellationToken cancellationToken)
    {
        var link = await db.ChargeDocuments.AsNoTracking().SingleOrDefaultAsync(d => d.ChargeId == chargeId && d.Kind == kind, cancellationToken);
        if (link is null)
        {
            return Missing;
        }

        return await issuer.RunAsync(link.IssuerTenantId, async services =>
        {
            var service = services.GetRequiredService<IElectronicDocumentService>();
            var electronic = await service.GetByDocumentAsync(link.DocumentId, cancellationToken);
            return electronic.IsSuccess ? await read(electronic.Value, service, cancellationToken) : Missing;
        });
    }
}
