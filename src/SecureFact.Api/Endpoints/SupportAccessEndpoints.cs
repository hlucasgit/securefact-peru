using SecureFact.Identity.Contracts;

namespace SecureFact.Api.Endpoints;

internal static class SupportAccessEndpoints
{
    public sealed record GrantBody(int? Hours, string? Note);

    public sealed record EnterBody(Guid TenantId, string Reason);

    public static void MapSupportAccessEndpoints(this IEndpointRouteBuilder app)
    {
        // The owner of an account decides who supports it, for how long, and takes it back whenever (ADR-069).
        var grants = app.MapGroup("/api/v1/support-access").WithTags("Support access");

        grants.MapGet(string.Empty, async (ISupportAccess service, HttpContext http, CancellationToken ct) =>
            (await service.ListGrantsAsync(ct)).ToHttp(http)).RequireAuthorization(Permissions.SupportGrant);

        grants.MapPost(string.Empty, async (GrantBody body, ISupportAccess service, HttpContext http, CancellationToken ct) =>
            (await service.GrantAsync(body.Hours, body.Note, ct)).ToHttp(http, created => Results.Created($"/api/v1/support-access/{created.Id}", created)))
            .RequireAuthorization(Permissions.SupportGrant);

        grants.MapPost("/{id:guid}/revoke", async (Guid id, ISupportAccess service, HttpContext http, CancellationToken ct) =>
            (await service.RevokeAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.SupportGrant);

        // The person who supports: which accounts they may enter now, and the entry with the reason.
        var sessions = app.MapGroup("/api/v1/support-sessions").WithTags("Support access");

        sessions.MapGet("/available", async (ISupportAccess service, HttpContext http, CancellationToken ct) =>
            (await service.AvailableAsync(ct)).ToHttp(http)).RequireAuthorization(Permissions.SupportAccess);

        sessions.MapPost(string.Empty, async (EnterBody body, ISupportAccess service, HttpContext http, CancellationToken ct) =>
            (await service.EnterAsync(body.TenantId, body.Reason, new ClientInfo(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString()), ct)).ToHttp(http))
            .RequireAuthorization(Permissions.SupportAccess);
    }
}
