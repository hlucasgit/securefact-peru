using SecureFact.SharedKernel.Results;

namespace SecureFact.Webhooks.Contracts;

/// <summary>The events a webhook can subscribe to (ADR-067). The names are part of the public contract and never change; new ones are added.</summary>
public static class WebhookEvents
{
    /// <summary>A document was numbered and stored (it is not yet at SUNAT).</summary>
    public const string DocumentIssued = "document.issued";

    /// <summary>SUNAT accepted the document, with or without observations.</summary>
    public const string DocumentAccepted = "document.accepted";

    /// <summary>SUNAT rejected the document.</summary>
    public const string DocumentRejected = "document.rejected";

    /// <summary>A test that the owner of the webhook asks for. It cannot be subscribed to: it is sent to the endpoint that asks.</summary>
    public const string Ping = "webhook.ping";

    /// <summary>What can be subscribed to.</summary>
    public static IReadOnlyList<string> Subscribable { get; } = [DocumentIssued, DocumentAccepted, DocumentRejected];
}

public enum WebhookDeliveryState
{
    /// <summary>Waiting for its first attempt or for the next one.</summary>
    Pending,

    /// <summary>The endpoint answered with a 2xx.</summary>
    Delivered,

    /// <summary>The last attempt failed and another is scheduled.</summary>
    Failed,

    /// <summary>It failed on every attempt (or the endpoint is gone): only a person sends it again.</summary>
    Dead,
}

/// <param name="SecretHint">The last characters of the secret, to know which one an endpoint has. The secret itself is shown once.</param>
/// <param name="ConsecutiveFailures">Failed attempts since the last success. At 40 the endpoint is disabled.</param>
public sealed record WebhookEndpointDto(
    Guid Id, string Url, string? Description, IReadOnlyList<string> Events, bool IsActive, string SecretHint, int ConsecutiveFailures, DateTimeOffset CreatedAt, DateTimeOffset? DisabledAt, string? DisabledReason);

public sealed record WebhookInput(string Url, string? Description, IReadOnlyList<string> Events, bool IsActive = true);

/// <param name="Secret">Signs what is sent (<c>whsec_…</c>). It is returned only here and when it is rotated.</param>
public sealed record CreatedWebhook(WebhookEndpointDto Endpoint, string Secret);

public sealed record WebhookDeliveryDto(
    Guid Id, Guid EndpointId, Guid EventId, string EventType, WebhookDeliveryState State, int Attempts, DateTimeOffset? NextAttemptAt, int? LastStatusCode, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? DeliveredAt);

/// <summary>
/// Webhooks of a tenant (ADR-067): where the platform tells a program that something happened. The tenant registers an https address and the events it wants; each event is sent as a signed POST
/// and tried again with a growing wait until the address answers. Everything here is of the tenant of the caller.
/// </summary>
public interface IWebhooks
{
    Task<Result<IReadOnlyList<WebhookEndpointDto>>> ListAsync(CancellationToken cancellationToken);

    Task<Result<CreatedWebhook>> CreateAsync(WebhookInput input, CancellationToken cancellationToken);

    /// <summary>Changes address, events or whether it is on. Turning on an endpoint that was disabled for failing clears its failure count.</summary>
    Task<Result<WebhookEndpointDto>> UpdateAsync(Guid id, WebhookInput input, CancellationToken cancellationToken);

    /// <summary>Removes the endpoint and its deliveries.</summary>
    Task<Result<Unit>> DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Makes a new secret and shows it. The old one stops signing at once.</summary>
    Task<Result<CreatedWebhook>> RotateSecretAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Sends a <see cref="WebhookEvents.Ping"/> to the endpoint now and answers how it went.</summary>
    Task<Result<WebhookDeliveryDto>> SendTestAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<WebhookDeliveryDto>>> ListDeliveriesAsync(Guid id, WebhookDeliveryState? state, int skip, int take, CancellationToken cancellationToken);

    /// <summary>Puts a delivery that is not pending back to be sent, with its attempts counted from zero.</summary>
    Task<Result<WebhookDeliveryDto>> RedeliverAsync(Guid deliveryId, CancellationToken cancellationToken);
}

/// <summary>The background work of the webhooks: sends the deliveries that are due. Platform scope.</summary>
public interface IWebhookDispatcher
{
    /// <summary>Sends what is due at <paramref name="now"/> and returns how many deliveries it attempted.</summary>
    Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
