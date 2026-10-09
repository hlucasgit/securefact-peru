using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Platform.Security;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Webhooks.Contracts;
using SecureFact.Webhooks.Domain;
using SecureFact.Webhooks.Infrastructure;
using Microsoft.Extensions.Options;

namespace SecureFact.Webhooks.Application;

internal sealed class WebhookService(
    WebhooksDbContext db, IDataScope scope, ICurrentUser actor, TimeProvider clock, IAuditTrail audit, ISecretProtector protector, IOptions<WebhookOptions> options, DeliveryAttempt attempts) : IWebhooks
{
    internal const string SecretPurpose = "webhook:secret";
    private const int MaxEndpoints = 10;
    private const int MaxDescriptionLength = 200;
    private const int MaxPageSize = 100;
    private const int SecretBytes = 32;
    private const int HintLength = 4;

    private static readonly Error Missing = Error.NotFound(ErrorCodes.WebhookNotFound, "Webhook no encontrado", "El webhook no existe o no es visible para este contexto.");

    public async Task<Result<IReadOnlyList<WebhookEndpointDto>>> ListAsync(CancellationToken cancellationToken)
    {
        if (RequireTenant() is { } denied)
        {
            return denied;
        }

        var endpoints = await db.Endpoints.AsNoTracking().OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(cancellationToken);
        return endpoints.Select(ToDto).ToList();
    }

    public async Task<Result<CreatedWebhook>> CreateAsync(WebhookInput input, CancellationToken cancellationToken)
    {
        if (RequireTenant() is { } denied)
        {
            return denied;
        }

        if (Validate(input) is { } invalid)
        {
            return invalid;
        }

        if (await db.Endpoints.CountAsync(cancellationToken) >= MaxEndpoints)
        {
            return Error.Conflict(ErrorCodes.WebhookLimit, "Demasiados webhooks", $"Una cuenta tiene hasta {MaxEndpoints} webhooks: borre alguno para crear otro.");
        }

        var secret = NewSecret();
        var endpoint = WebhookEndpoint.Create(
            scope.Current!.Value.Value, input.Url.Trim(), Clean(input.Description), input.Events.Distinct(StringComparer.Ordinal), input.IsActive, Protect(secret), secret[^HintLength..], clock.GetUtcNow());
        db.Endpoints.Add(endpoint);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.WebhookCreated, "webhook", endpoint.Id.ToString("D"), endpoint.TenantId,
                NewValues: new Dictionary<string, object?> { ["url"] = endpoint.Url, ["events"] = endpoint.Events.ToList(), ["active"] = endpoint.IsActive }),
            cancellationToken);
        return new CreatedWebhook(ToDto(endpoint), secret);
    }

    public async Task<Result<WebhookEndpointDto>> UpdateAsync(Guid id, WebhookInput input, CancellationToken cancellationToken)
    {
        if (RequireTenant() is { } denied)
        {
            return denied;
        }

        if (Validate(input) is { } invalid)
        {
            return invalid;
        }

        var endpoint = await db.Endpoints.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (endpoint is null)
        {
            return Missing;
        }

        endpoint.Update(input.Url.Trim(), Clean(input.Description), input.Events.Distinct(StringComparer.Ordinal), input.IsActive, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.WebhookUpdated, "webhook", id.ToString("D"), endpoint.TenantId,
                NewValues: new Dictionary<string, object?> { ["url"] = endpoint.Url, ["events"] = endpoint.Events.ToList(), ["active"] = endpoint.IsActive }),
            cancellationToken);
        return ToDto(endpoint);
    }

    public async Task<Result<Unit>> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (RequireTenant() is { } denied)
        {
            return denied;
        }

        var endpoint = await db.Endpoints.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (endpoint is null)
        {
            return Missing;
        }

        await db.Deliveries.Where(d => d.EndpointId == id).ExecuteDeleteAsync(cancellationToken);
        db.Endpoints.Remove(endpoint);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.WebhookDeleted, "webhook", id.ToString("D"), endpoint.TenantId, OldValues: new Dictionary<string, object?> { ["url"] = endpoint.Url }), cancellationToken);
        return Unit.Value;
    }

    public async Task<Result<CreatedWebhook>> RotateSecretAsync(Guid id, CancellationToken cancellationToken)
    {
        if (RequireTenant() is { } denied)
        {
            return denied;
        }

        var endpoint = await db.Endpoints.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (endpoint is null)
        {
            return Missing;
        }

        var secret = NewSecret();
        endpoint.ReplaceSecret(Protect(secret), secret[^HintLength..], clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.WebhookSecretRotated, "webhook", id.ToString("D"), endpoint.TenantId), cancellationToken);
        return new CreatedWebhook(ToDto(endpoint), secret);
    }

    public async Task<Result<WebhookDeliveryDto>> SendTestAsync(Guid id, CancellationToken cancellationToken)
    {
        if (RequireTenant() is { } denied)
        {
            return denied;
        }

        var endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (endpoint is null)
        {
            return Missing;
        }

        if (!endpoint.IsActive)
        {
            return Error.Validation(ErrorCodes.InvalidWebhook, "Webhook desactivado", "Active el webhook para probarlo.");
        }

        var now = clock.GetUtcNow();
        var eventId = Guid.CreateVersion7();
        var payload = WebhookPayloads.Ping(eventId, endpoint.TenantId, now);
        var delivery = WebhookDelivery.Create(endpoint.TenantId, endpoint.Id, eventId, WebhookEvents.Ping, payload, now);
        db.Deliveries.Add(delivery);
        await db.SaveChangesAsync(cancellationToken);

        // The test is sent now and its outcome is the answer, so the person sees at once whether the address works.
        await attempts.RunAsync(delivery.Id, now, claimed: false, cancellationToken);
        var after = await db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id, cancellationToken);
        return ToDto(after);
    }

    public async Task<Result<IReadOnlyList<WebhookDeliveryDto>>> ListDeliveriesAsync(Guid id, WebhookDeliveryState? state, int skip, int take, CancellationToken cancellationToken)
    {
        if (RequireTenant() is { } denied)
        {
            return denied;
        }

        if (!await db.Endpoints.AnyAsync(e => e.Id == id, cancellationToken))
        {
            return Missing;
        }

        var query = db.Deliveries.AsNoTracking().Where(d => d.EndpointId == id);
        if (state is { } wanted)
        {
            query = query.Where(d => d.State == wanted);
        }

        var deliveries = await query.OrderByDescending(d => d.CreatedAt).ThenBy(d => d.Id).Skip(Math.Max(skip, 0)).Take(Math.Clamp(take, 1, MaxPageSize)).ToListAsync(cancellationToken);
        return deliveries.Select(ToDto).ToList();
    }

    public async Task<Result<WebhookDeliveryDto>> RedeliverAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        if (RequireTenant() is { } denied)
        {
            return denied;
        }

        var delivery = await db.Deliveries.SingleOrDefaultAsync(d => d.Id == deliveryId, cancellationToken);
        if (delivery is null)
        {
            return Error.NotFound(ErrorCodes.WebhookNotFound, "Entrega no encontrada", "La entrega no existe o no es visible para este contexto.");
        }

        if (delivery.State == WebhookDeliveryState.Pending)
        {
            return Error.Conflict(ErrorCodes.InvalidWebhook, "Entrega pendiente", "La entrega ya está esperando su turno.");
        }

        if (!await db.Endpoints.AnyAsync(e => e.Id == delivery.EndpointId && e.IsActive, cancellationToken))
        {
            return Error.Validation(ErrorCodes.InvalidWebhook, "Webhook desactivado", "Active el webhook para volver a enviar.");
        }

        delivery.Requeue(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(delivery);
    }

    private static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private byte[] Protect(string secret) => protector.Protect(Encoding.UTF8.GetBytes(secret), SecretPurpose);

    private Error? Validate(WebhookInput input)
    {
        if (WebhookTargets.Validate(input.Url, options.Value.AllowLocalTargets) is { } reason)
        {
            return Error.Validation(ErrorCodes.InvalidWebhook, "Dirección inválida", reason);
        }

        var events = (input.Events ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (events.Count == 0 || events.Any(e => !WebhookEvents.Subscribable.Contains(e, StringComparer.Ordinal)))
        {
            return Error.Validation(ErrorCodes.InvalidWebhook, "Eventos inválidos", $"Indique al menos un evento de estos: {string.Join(", ", WebhookEvents.Subscribable)}.");
        }

        return input.Description?.Trim().Length > MaxDescriptionLength
            ? Error.Validation(ErrorCodes.InvalidWebhook, "Descripción demasiado larga", $"La descripción tiene hasta {MaxDescriptionLength} caracteres.")
            : null;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    internal static WebhookEndpointDto ToDto(WebhookEndpoint e) =>
        new(e.Id, e.Url, e.Description, e.Events, e.IsActive, e.SecretHint, e.ConsecutiveFailures, e.CreatedAt, e.DisabledAt, e.DisabledReason);

    internal static WebhookDeliveryDto ToDto(WebhookDelivery d) =>
        new(d.Id, d.EndpointId, d.EventId, d.EventType, d.State, d.Attempts, d.NextAttemptAt, d.LastStatusCode, d.LastError, d.CreatedAt, d.DeliveredAt);

    /// <summary>The webhooks of a tenant are managed by a person of that tenant. Platform staff and programs with a key do not (a key has no permission to).</summary>
    private Error? RequireTenant() =>
        scope.Kind == DataScopeKind.Tenant && scope.Current is not null && !actor.IsPlatform
            ? null
            : Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Los webhooks son de una cuenta: los administra una persona de esa cuenta.");
}

/// <summary>The bodies that are sent. They are public contract: the names and the shape do not change, fields are added.</summary>
internal static class WebhookPayloads
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Ping(Guid eventId, Guid tenantId, DateTimeOffset now) =>
        JsonSerializer.Serialize(new { id = eventId, type = WebhookEvents.Ping, apiVersion = "v1", createdAt = now, tenantId, data = new { message = "Prueba de conexión de SecureFact Perú." } }, Json);

    public static string Event(Guid eventId, string type, Guid tenantId, DateTimeOffset createdAt, object data) =>
        JsonSerializer.Serialize(new { id = eventId, type, apiVersion = "v1", createdAt, tenantId, data }, Json);
}
