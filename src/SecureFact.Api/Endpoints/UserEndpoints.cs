using SecureFact.Identity.Contracts;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Api.Endpoints;

internal static class UserEndpoints
{
    public sealed record RoleBody(string Role);

    public sealed record CreateTenantBody(string Name, TenantEnvironment Environment);

    public sealed record TenantStatusBody(TenantStatus Status, string Reason);

    public static void MapUserAndTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/v1/users").WithTags("Users");

        users.MapGet(string.Empty, async (int? skip, int? take, Guid? tenantId, IUserAdministration admin, CancellationToken ct) =>
            Results.Ok(await admin.ListAsync(skip ?? 0, take ?? 50, tenantId, ct))).RequireAuthorization(Permissions.UsersRead);

        users.MapGet("/{id:guid}", async (Guid id, IUserAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.GetAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.UsersRead);

        users.MapPost(string.Empty, async (CreateUserRequest body, IUserAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.CreateAsync(body, ct)).ToHttp(http, dto => Results.Created($"/api/v1/users/{dto.Id}", dto)))
            .RequireAuthorization(Permissions.UsersManage);

        users.MapPost("/{id:guid}/roles", async (Guid id, RoleBody body, IUserAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.AssignRoleAsync(id, body.Role, ct)).ToHttp(http)).RequireAuthorization(Permissions.UsersManage);

        users.MapDelete("/{id:guid}/roles/{role}", async (Guid id, string role, IUserAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.RemoveRoleAsync(id, role, ct)).ToHttp(http)).RequireAuthorization(Permissions.UsersManage);

        users.MapPost("/{id:guid}/deactivate", async (Guid id, IUserAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.DeactivateAsync(id, ct)).ToNoContent(http)).RequireAuthorization(Permissions.UsersManage);

        users.MapPost("/{id:guid}/sessions/revoke", async (Guid id, IUserAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.RevokeSessionsAsync(id, ct)).ToNoContent(http)).RequireAuthorization(Permissions.SessionsRevoke);

        var tenants = app.MapGroup("/api/v1").WithTags("Tenants");

        tenants.MapPost("/platform/tenants", async (CreateTenantBody body, ITenantAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.CreateAsync(new CreateTenantRequest(body.Name, body.Environment), ct))
                .ToHttp(http, dto => Results.Created($"/api/v1/platform/tenants/{dto.Id}", dto)))
            .RequireAuthorization(Permissions.TenantsCreate);

        tenants.MapGet("/platform/tenants", async (string? search, TenantStatus? status, int? skip, int? take, ITenantAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.ListAsync(search, status, skip ?? 0, take ?? 50, ct)).ToHttp(http)).RequireAuthorization(Permissions.TenantsRead);

        tenants.MapPost("/platform/tenants/{id:guid}/status", async (Guid id, TenantStatusBody body, ITenantAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.ChangeStatusAsync(new TenantId(id), body.Status, body.Reason, ct)).ToHttp(http)).RequireAuthorization(Permissions.TenantsManage);

        tenants.MapGet("/platform/tenants/{id:guid}", async (Guid id, ITenantAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.GetAsync(new TenantId(id), ct)).ToHttp(http)).RequireAuthorization(Permissions.TenantsRead);

        tenants.MapGet("/tenants/current", async (ICurrentUser user, ITenantAdministration admin, HttpContext http, CancellationToken ct) =>
            user.TenantId is { } tenantId ? (await admin.GetAsync(tenantId, ct)).ToHttp(http) : Results.NotFound())
            .RequireAuthorization(Permissions.TenantsRead);
    }
}
