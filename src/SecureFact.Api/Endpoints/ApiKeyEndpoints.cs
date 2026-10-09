using SecureFact.Identity.Contracts;

namespace SecureFact.Api.Endpoints;

internal static class ApiKeyEndpoints
{
    public sealed record CreateKeyBody(string Name, string Role, DateTimeOffset? ExpiresAt);

    public static void MapApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var keys = app.MapGroup("/api/v1/api-keys").WithTags("API keys");

        // Only a person with the permission manages the keys: a key itself never has it, so a leaked key cannot make another.
        keys.MapGet(string.Empty, async (IApiKeys service, HttpContext http, CancellationToken ct) =>
            (await service.ListAsync(ct)).ToHttp(http)).RequireAuthorization(Permissions.ApiKeysManage);

        keys.MapPost(string.Empty, async (CreateKeyBody body, IApiKeys service, HttpContext http, CancellationToken ct) =>
            (await service.CreateAsync(body.Name, body.Role, body.ExpiresAt, ct)).ToHttp(http, created => Results.Created($"/api/v1/api-keys/{created.Key.Id}", created)))
            .RequireAuthorization(Permissions.ApiKeysManage);

        keys.MapPost("/{id:guid}/revoke", async (Guid id, IApiKeys service, HttpContext http, CancellationToken ct) =>
            (await service.RevokeAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.ApiKeysManage);

        keys.MapGet("/roles", () => Results.Ok(IApiKeys.AssignableRoles)).RequireAuthorization(Permissions.ApiKeysManage);
    }
}
