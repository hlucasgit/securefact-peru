using SecureFact.Identity.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Api.Endpoints;

internal static class ResellerEndpoints
{
    public sealed record ResellerBody(string Name, bool? IsActive);

    public sealed record AssignResellerBody(Guid? ResellerId);

    public sealed record ResellerTenantBody(string Name, TenantEnvironment Environment, Guid? PlanId);

    public sealed record OwnerBody(string Email, string DisplayName, string Password);

    public sealed record PlanChoiceBody(Guid PlanId);

    public static void MapResellerEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").WithTags("Resellers");

        // ---------- platform staff ----------

        api.MapGet("/platform/resellers", async (IResellerAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.ListAsync(ct)).ToHttp(http)).RequireAuthorization(Permissions.TenantsRead);

        api.MapPost("/platform/resellers", async (ResellerBody body, IResellerAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.CreateAsync(body.Name, ct)).ToHttp(http, dto => Results.Created($"/api/v1/platform/resellers/{dto.Id}", dto))).RequireAuthorization(Permissions.TenantsManage);

        api.MapPut("/platform/resellers/{id:guid}", async (Guid id, ResellerBody body, IResellerAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.UpdateAsync(id, body.Name, body.IsActive ?? true, ct)).ToHttp(http)).RequireAuthorization(Permissions.TenantsManage);

        api.MapPost("/platform/tenants/{id:guid}/reseller", async (Guid id, AssignResellerBody body, IResellerAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.AssignTenantAsync(new TenantId(id), body.ResellerId, ct)).ToHttp(http)).RequireAuthorization(Permissions.TenantsManage);

        // ---------- the reseller's own view: the reseller always comes from the token ----------

        api.MapGet("/reseller", async (ICurrentUser user, IResellerAdministration admin, HttpContext http, CancellationToken ct) =>
            user.ResellerId is { } reseller ? (await admin.GetOwnAsync(reseller, ct)).ToHttp(http) : Results.Forbid()).RequireAuthorization(Permissions.ResellerTenantsRead);

        api.MapGet("/reseller/tenants", async (string? search, int? skip, int? take, ICurrentUser user, IResellerAdministration admin, HttpContext http, CancellationToken ct) =>
            user.ResellerId is { } reseller ? (await admin.ListTenantsAsync(reseller, search, skip ?? 0, take ?? 50, ct)).ToHttp(http) : Results.Forbid())
            .RequireAuthorization(Permissions.ResellerTenantsRead);

        api.MapGet("/reseller/tenants/{id:guid}", async (Guid id, ICurrentUser user, IResellerAdministration admin, HttpContext http, CancellationToken ct) =>
            user.ResellerId is { } reseller ? (await admin.GetTenantAsync(reseller, new TenantId(id), ct)).ToHttp(http) : Results.Forbid())
            .RequireAuthorization(Permissions.ResellerTenantsRead);

        api.MapGet("/reseller/tenants/{id:guid}/usage", async (Guid id, ICurrentUser user, IResellerAdministration admin, PlanEndpoints.TenantUsageReader usage, HttpContext http, CancellationToken ct) =>
        {
            if (user.ResellerId is not { } reseller)
            {
                return Results.Forbid();
            }

            var tenant = await admin.GetTenantAsync(reseller, new TenantId(id), ct);
            return tenant.IsSuccess ? (await usage.ReadAsync(tenant.Value.Id, ct)).ToHttp(http) : tenant.ToHttp(http);
        }).RequireAuthorization(Permissions.ResellerTenantsRead);

        api.MapPost("/reseller/tenants", async (ResellerTenantBody body, ICurrentUser user, IResellerAdministration admin, HttpContext http, CancellationToken ct) =>
            user.ResellerId is { } reseller
                ? (await admin.CreateTenantAsync(reseller, new ResellerTenantRequest(body.Name, body.Environment, body.PlanId), ct))
                    .ToHttp(http, dto => Results.Created($"/api/v1/reseller/tenants/{dto.Id}", dto))
                : Results.Forbid()).RequireAuthorization(Permissions.ResellerTenantsCreate);

        // The first owner of a tenant of the reseller. It is allowed once: while the tenant has no active user. After that the customer administers its own users.
        api.MapPost("/reseller/tenants/{id:guid}/owner", async (Guid id, OwnerBody body, ICurrentUser user, IResellerAdministration admin, IUserAdministration users, HttpContext http, CancellationToken ct) =>
        {
            if (user.ResellerId is not { } reseller)
            {
                return Results.Forbid();
            }

            var tenant = await admin.GetTenantAsync(reseller, new TenantId(id), ct);
            if (!tenant.IsSuccess)
            {
                return tenant.ToHttp(http);
            }

            if (await users.CountActiveAsync(id, ct) > 0)
            {
                return Result<UserDto>.Failure(Error.Conflict(ErrorCodes.RoleNotAssignable, "La cuenta ya tiene usuarios", "El propietario se crea una sola vez; después la cuenta administra sus usuarios.")).ToHttp(http);
            }

            return (await users.CreateTenantOwnerAsync(id, body.Email, body.DisplayName, body.Password, ct)).ToHttp(http, dto => Results.Created($"/api/v1/users/{dto.Id}", dto));
        }).RequireAuthorization(Permissions.ResellerTenantsCreate);

        api.MapGet("/reseller/plans", async (ICurrentUser user, IResellerAdministration admin, HttpContext http, CancellationToken ct) =>
            user.ResellerId is { } reseller ? (await admin.ListPlansAsync(reseller, ct)).ToHttp(http) : Results.Forbid()).RequireAuthorization(Permissions.ResellerTenantsRead);

        api.MapPost("/reseller/tenants/{id:guid}/plan", async (Guid id, PlanChoiceBody body, ICurrentUser user, IResellerAdministration admin, HttpContext http, CancellationToken ct) =>
            user.ResellerId is { } reseller ? (await admin.AssignPlanAsync(reseller, new TenantId(id), body.PlanId, ct)).ToHttp(http) : Results.Forbid())
            .RequireAuthorization(Permissions.ResellerTenantsManage);
    }
}
