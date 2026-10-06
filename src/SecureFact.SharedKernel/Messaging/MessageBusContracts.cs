namespace SecureFact.SharedKernel.Messaging;

/// <summary>
/// An integration event on its way out of the platform to systems that subscribe to it (ADR-035). It carries the identifier of the outbox message it comes from,
/// which is stable across retries so that a subscriber can drop the duplicates an at-least-once delivery may produce.
/// </summary>
/// <param name="MessageId">Identifier of the outbox message; the same value on every delivery of the same event.</param>
/// <param name="TenantId">Tenant that owns the event.</param>
/// <param name="EventType">Event type, e.g. <c>billing.document.issued</c>; it is the routing key.</param>
/// <param name="PayloadJson">The event as written by its module (JSON). It never carries secrets.</param>
/// <param name="OccurredAt">When the business change that caused the event was committed.</param>
public sealed record BusMessage(Guid MessageId, Guid TenantId, string EventType, string PayloadJson, DateTimeOffset OccurredAt);

/// <summary>
/// Publishes integration events to the message broker. The outbox calls it after the business change is committed, so a failure here is retried by the outbox
/// rather than lost; an implementation must therefore either confirm that the broker took the message or throw.
/// </summary>
public interface IMessageBus
{
    Task PublishAsync(BusMessage message, CancellationToken cancellationToken);
}
