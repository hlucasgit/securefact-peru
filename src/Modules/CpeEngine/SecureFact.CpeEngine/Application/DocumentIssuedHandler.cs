using System.Text.Json;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine.Contracts;
using SecureFact.SharedKernel.Messaging;

namespace SecureFact.CpeEngine.Application;

/// <summary>
/// Prepares the electronic document (UBL + signature) of every invoice and receipt Billing announces. Idempotent, as at-least-once delivery
/// requires: preparing a document twice returns the first result. Failures (for example a missing certificate) surface as exceptions so the
/// outbox retries with backoff and, in the end, leaves the message dead for an operator.
/// </summary>
internal sealed class DocumentIssuedHandler(IElectronicDocumentService electronicDocuments) : IIntegrationEventConsumer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string EventType => BillingEvents.DocumentIssued;

    public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var issued = JsonSerializer.Deserialize<DocumentIssuedEvent>(message.PayloadJson, Json)
            ?? throw new InvalidOperationException("The event payload is empty.");
        if (issued.TenantId != message.TenantId)
        {
            throw new InvalidOperationException("The event does not belong to the tenant of its outbox message.");
        }

        if (issued.DocumentTypeCode is not (DocumentTypes.Invoice or DocumentTypes.Receipt))
        {
            return; // nothing to prepare for other document types (yet)
        }

        var prepared = await electronicDocuments.PrepareAsync(issued.DocumentId, cancellationToken);
        if (!prepared.IsSuccess)
        {
            throw new InvalidOperationException($"{prepared.Error.Code}: {prepared.Error.Detail}");
        }
    }
}
