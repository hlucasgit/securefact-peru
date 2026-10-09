using SecureFact.Identity.Contracts;
using SecureFact.Webhooks.Contracts;

namespace SecureFact.Api.Endpoints;

internal static class WebhookEndpoints
{
    public sealed record WebhookBody(string Url, string? Description, IReadOnlyList<string> Events, bool? IsActive);

    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var hooks = app.MapGroup("/api/v1/webhooks").WithTags("Webhooks");

        // A person of the account manages the webhooks. No key has the permission, so a program cannot point the events of its account somewhere else.
        hooks.MapGet(string.Empty, async (IWebhooks service, HttpContext http, CancellationToken ct) =>
            (await service.ListAsync(ct)).ToHttp(http)).RequireAuthorization(Permissions.WebhooksManage);

        hooks.MapGet("/events", () => Results.Ok(WebhookEvents.Subscribable)).RequireAuthorization(Permissions.WebhooksManage);

        hooks.MapPost(string.Empty, async (WebhookBody body, IWebhooks service, HttpContext http, CancellationToken ct) =>
            (await service.CreateAsync(new WebhookInput(body.Url, body.Description, body.Events ?? [], body.IsActive ?? true), ct))
            .ToHttp(http, created => Results.Created($"/api/v1/webhooks/{created.Endpoint.Id}", created))).RequireAuthorization(Permissions.WebhooksManage);

        hooks.MapPut("/{id:guid}", async (Guid id, WebhookBody body, IWebhooks service, HttpContext http, CancellationToken ct) =>
            (await service.UpdateAsync(id, new WebhookInput(body.Url, body.Description, body.Events ?? [], body.IsActive ?? true), ct)).ToHttp(http)).RequireAuthorization(Permissions.WebhooksManage);

        hooks.MapDelete("/{id:guid}", async (Guid id, IWebhooks service, HttpContext http, CancellationToken ct) =>
            (await service.DeleteAsync(id, ct)).ToNoContent(http)).RequireAuthorization(Permissions.WebhooksManage);

        hooks.MapPost("/{id:guid}/rotate-secret", async (Guid id, IWebhooks service, HttpContext http, CancellationToken ct) =>
            (await service.RotateSecretAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.WebhooksManage);

        hooks.MapPost("/{id:guid}/test", async (Guid id, IWebhooks service, HttpContext http, CancellationToken ct) =>
            (await service.SendTestAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.WebhooksManage);

        hooks.MapGet("/{id:guid}/deliveries", async (Guid id, WebhookDeliveryState? state, int? skip, int? take, IWebhooks service, HttpContext http, CancellationToken ct) =>
            (await service.ListDeliveriesAsync(id, state, skip ?? 0, take ?? 50, ct)).ToHttp(http)).RequireAuthorization(Permissions.WebhooksManage);

        hooks.MapPost("/deliveries/{deliveryId:guid}/redeliver", async (Guid deliveryId, IWebhooks service, HttpContext http, CancellationToken ct) =>
            (await service.RedeliverAsync(deliveryId, ct)).ToHttp(http)).RequireAuthorization(Permissions.WebhooksManage);
    }
}
