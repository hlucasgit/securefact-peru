using SecureFact.Audit.Contracts;
using SecureFact.Identity.Contracts;

namespace SecureFact.Api.Endpoints;

internal static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/audit").WithTags("Audit");

        group.MapGet(string.Empty, async (
            Guid? tenantId, string? action, string? entityType, string? entityId, int? skip, int? take,
            IAuditQuery audit, CancellationToken ct) =>
            Results.Ok(await audit.ListAsync(tenantId, action, entityType, entityId, skip ?? 0, take ?? 50, ct)))
            .RequireAuthorization(Permissions.AuditRead);

        group.MapPost("/verify", async (Guid? tenantId, IAuditQuery audit, CancellationToken ct) =>
            Results.Ok(await audit.VerifyAsync(tenantId, ct)))
            .RequireAuthorization(Permissions.AuditRead);
    }
}
