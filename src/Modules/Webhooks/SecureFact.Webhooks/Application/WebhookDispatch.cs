using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Platform.Security;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Messaging;
using SecureFact.Webhooks.Contracts;
using SecureFact.Webhooks.Domain;
using SecureFact.Webhooks.Infrastructure;

namespace SecureFact.Webhooks.Application;

/// <summary>
/// One attempt to send a delivery, in the scope of its tenant. A 2xx is delivered; anything else (another status, no answer, a refused address) counts as failed and is tried again later, up to
/// <see cref="MaxAttempts"/>. A 410 means the receiver is gone for good: the delivery dies and the endpoint is switched off.
/// </summary>
internal sealed partial class DeliveryAttempt(WebhooksDbContext db, ISecretProtector protector, IHttpClientFactory http, IOptions<WebhookOptions> options, TimeProvider clock, ILogger<DeliveryAttempt> logger)
{
    public const int MaxAttempts = 8;
    public const string ClientName = "webhooks";

    /// <summary>The wait after the first failed attempt, the second and so on. After the eighth failure the delivery is dead.</summary>
    private static readonly TimeSpan[] Waits = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromHours(24)];

    /// <param name="claimed">True when the dispatcher already counted this attempt when it took the delivery; a test the person asks for counts its own.</param>
    public async Task RunAsync(Guid deliveryId, DateTimeOffset now, bool claimed, CancellationToken cancellationToken)
    {
        var delivery = await db.Deliveries.SingleOrDefaultAsync(d => d.Id == deliveryId, cancellationToken);
        if (delivery is null)
        {
            return;
        }

        if (!claimed)
        {
            delivery.CountAttempt();
        }

        var endpoint = await db.Endpoints.SingleOrDefaultAsync(e => e.Id == delivery.EndpointId, cancellationToken);
        if (endpoint is not { IsActive: true })
        {
            delivery.MarkFailed(null, "El webhook está desactivado o ya no existe.", null);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var (status, error) = await SendAsync(endpoint, delivery, now, cancellationToken);
        if (status is >= 200 and < 300)
        {
            delivery.MarkDelivered(status.Value, clock.GetUtcNow());
            endpoint.RecordSuccess(clock.GetUtcNow());
        }
        else if (status == 410)
        {
            delivery.MarkFailed(status, "El destino respondió 410: ya no existe.", null);
            endpoint.Disable("El destino respondió 410 (ya no existe)", clock.GetUtcNow());
        }
        else
        {
            DateTimeOffset? next = delivery.Attempts >= MaxAttempts ? null : clock.GetUtcNow() + Waits[Math.Min(delivery.Attempts, Waits.Length) - 1];
            delivery.MarkFailed(status, error ?? $"HTTP {status}", next);
            endpoint.RecordFailure(clock.GetUtcNow());
            LogFailed(logger, delivery.Id, status, next is null);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<(int? Status, string? Error)> SendAsync(WebhookEndpoint endpoint, WebhookDelivery delivery, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string secret;
        try
        {
            secret = Encoding.UTF8.GetString(protector.Unprotect(endpoint.SecretCiphertext, WebhookService.SecretPurpose));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return (null, "No se pudo leer el secreto del webhook: rótelo.");
        }

        var timestamp = now.ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url) { Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("User-Agent", "SecureFact-Webhooks/1");
        request.Headers.TryAddWithoutValidation("X-SecureFact-Event", delivery.EventType);
        request.Headers.TryAddWithoutValidation("X-SecureFact-Delivery", delivery.Id.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-SecureFact-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-SecureFact-Signature", WebhookSignature.Sign(secret, timestamp, delivery.Payload));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(options.Value.RequestTimeoutSeconds, 1)));
        try
        {
            using var response = await http.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return ((int)response.StatusCode, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "Tiempo de espera agotado.");
        }
        catch (HttpRequestException)
        {
            // Never the text of the exception: it can name the address that was refused, which is the receiver's business and not worth leaking into a tenant's screen.
            return (null, "No se pudo conectar con la dirección.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId} failed (status {Status}); dead: {Dead}.")]
    private static partial void LogFailed(ILogger logger, Guid deliveryId, int? status, bool dead);
}

/// <summary>Sends what is due: takes the deliveries across tenants (platform scope, with a lease so several workers do not take the same one) and attempts each in the scope of its tenant.</summary>
internal sealed partial class WebhookDispatcher(IServiceScopeFactory scopes, ILogger<WebhookDispatcher> logger) : IWebhookDispatcher
{
    private const int BatchSize = 50;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private sealed record Claimed(Guid Id, Guid TenantId);

    public async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<Claimed> claimed;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<DataScope>().UsePlatform("webhooks: claim the deliveries that are due");
            var db = scope.ServiceProvider.GetRequiredService<WebhooksDbContext>();
            var leaseUntil = now + Lease;
            claimed = await db.Database.SqlQuery<Claimed>($"""
                UPDATE webhook.delivery d
                SET attempts = d.attempts + 1, next_attempt_at = {leaseUntil}
                WHERE d.id IN (
                    SELECT id FROM webhook.delivery
                    WHERE state IN ('Pending', 'Failed') AND next_attempt_at <= {now}
                    ORDER BY next_attempt_at
                    LIMIT {BatchSize}
                    FOR UPDATE SKIP LOCKED)
                RETURNING d.id AS "Id", d.tenant_id AS "TenantId"
                """).ToListAsync(cancellationToken);
        }

        foreach (var item in claimed)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<DataScope>().UseTenant(new SecureFact.SharedKernel.Domain.TenantId(item.TenantId));
                await scope.ServiceProvider.GetRequiredService<DeliveryAttempt>().RunAsync(item.Id, now, claimed: true, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One delivery that fails unexpectedly must not stop the rest; its lease expires and it is taken again. Only its id goes to the log.
                LogUnexpected(logger, exception, item.Id);
            }
        }

        return claimed.Count;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook delivery {DeliveryId} failed unexpectedly.")]
    private static partial void LogUnexpected(ILogger logger, Exception exception, Guid deliveryId);
}

/// <summary>
/// Turns the events of the platform into deliveries to the endpoints that asked for them (ADR-067). One consumer for each event type that is announced (not a consumer of every type: an event
/// that nobody consumes must keep failing visibly in the outbox), run in the scope of the tenant of the event. It is idempotent (an event is delivered to an endpoint once, however many times
/// the outbox hands it over) and it writes only deliveries: nothing leaves the platform from here.
/// </summary>
internal sealed class WebhookFanOut(string eventType, WebhooksDbContext db, IElectronicDocumentService electronic, TimeProvider clock) : IIntegrationEventConsumer
{
    /// <summary>The events of the platform that become webhooks.</summary>
    public static IReadOnlyList<string> Handled { get; } = [BillingEvents.DocumentIssued, CpeEvents.DocumentAnswered];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The document types that are announced: the ones that go to SUNAT as their own file. Summaries and voided-document communications are not documents of the customer.</summary>
    private static readonly HashSet<string> DocumentTypes = ["01", "03", "07", "08"];

    public string EventType => eventType;

    public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var (type, data) = message.EventType == BillingEvents.DocumentIssued ? Issued(message) : await AnsweredAsync(message, cancellationToken);
        if (type is null || data is null)
        {
            return;
        }

        var endpoints = await db.Endpoints.AsNoTracking().Where(e => e.IsActive && e.Events.Contains(type)).Select(e => e.Id).ToListAsync(cancellationToken);
        if (endpoints.Count == 0)
        {
            return;
        }

        var payload = WebhookPayloads.Event(message.Id, type, message.TenantId, message.CreatedAt, data);
        var now = clock.GetUtcNow();
        foreach (var endpointId in endpoints)
        {
            if (await db.Deliveries.AnyAsync(d => d.EndpointId == endpointId && d.EventId == message.Id, cancellationToken))
            {
                continue;
            }

            db.Deliveries.Add(WebhookDelivery.Create(message.TenantId, endpointId, message.Id, type, payload, now));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static (string? Type, object? Data) Issued(OutboxMessage message)
    {
        var issued = JsonSerializer.Deserialize<DocumentIssuedEvent>(message.PayloadJson, Json);
        return issued is null || !DocumentTypes.Contains(issued.DocumentTypeCode)
            ? (null, null)
            : (WebhookEvents.DocumentIssued, new { documentId = issued.DocumentId, companyId = issued.CompanyId, documentTypeCode = issued.DocumentTypeCode, series = issued.Series, number = issued.Number });
    }

    private async Task<(string? Type, object? Data)> AnsweredAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var answered = JsonSerializer.Deserialize<ElectronicDocumentEventPayload>(message.PayloadJson, Json);
        if (answered is null || !DocumentTypes.Contains(answered.DocumentTypeCode))
        {
            return (null, null);
        }

        var document = await electronic.GetAsync(answered.ElectronicDocumentId, cancellationToken);
        if (!document.IsSuccess)
        {
            // The event is there but the document is not visible: leave the message to be retried instead of losing the announcement.
            throw new InvalidOperationException("The electronic document of the event could not be read.");
        }

        var value = document.Value;
        var type = value.State switch
        {
            EDocumentState.Accepted or EDocumentState.AcceptedWithObservations => WebhookEvents.DocumentAccepted,
            EDocumentState.Rejected => WebhookEvents.DocumentRejected,
            _ => null,
        };
        return type is null
            ? (null, null)
            : (type, new
            {
                documentId = value.DocumentId,
                electronicDocumentId = value.Id,
                companyId = value.CompanyId,
                documentTypeCode = value.DocumentTypeCode,
                series = value.Series,
                number = value.Number,
                issueDate = value.IssueDate,
                state = value.State.ToString(),
                sunat = new { code = value.CdrResponseCode, description = value.CdrDescription, observations = value.CdrObservations.Select(o => new { code = o.Code, message = o.Message }) },
            });
    }
}
