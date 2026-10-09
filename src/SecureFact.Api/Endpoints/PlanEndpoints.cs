using SecureFact.Billing.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Api.Endpoints;

internal static class PlanEndpoints
{
    public sealed record PlanBody(string Code, string Name, int? MaxCompanies, int? MaxUsers, int? MaxDocumentsPerMonth, bool? IsActive, Guid? ResellerId = null, bool? AllowsOverage = null);

    public sealed record AssignPlanBody(Guid PlanId);

    /// <summary>What is used against a limit; a null limit is unlimited.</summary>
    public sealed record UsageItem(int Used, int? Limit);

    public sealed record TenantUsageDto(PlanDto Plan, string Period, UsageItem Companies, UsageItem Users, UsageItem DocumentsThisMonth);

    private static readonly TimeZoneInfo Lima = TimeZoneInfo.FindSystemTimeZoneById("America/Lima");

    public static void MapPlanEndpoints(this IEndpointRouteBuilder app)
    {
        var plans = app.MapGroup("/api/v1").WithTags("Plans");

        plans.MapGet("/platform/plans", async (IPlanAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.ListAsync(ct)).ToHttp(http)).RequireAuthorization(Permissions.TenantsRead);

        plans.MapPost("/platform/plans", async (PlanBody body, IPlanAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.CreateAsync(ToInput(body), ct)).ToHttp(http, dto => Results.Created($"/api/v1/platform/plans/{dto.Id}", dto)))
            .RequireAuthorization(Permissions.TenantsManage);

        plans.MapPut("/platform/plans/{id:guid}", async (Guid id, PlanBody body, IPlanAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.UpdateAsync(id, ToInput(body), ct)).ToHttp(http)).RequireAuthorization(Permissions.TenantsManage);

        plans.MapPost("/platform/tenants/{id:guid}/plan", async (Guid id, AssignPlanBody body, IPlanAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.AssignAsync(new TenantId(id), body.PlanId, ct)).ToHttp(http)).RequireAuthorization(Permissions.TenantsManage);

        plans.MapGet("/platform/tenants/{id:guid}/usage", async (Guid id, ICurrentUser user, TenantUsageReader reader, HttpContext http, CancellationToken ct) =>
            user.IsPlatform ? (await reader.ReadAsync(new TenantId(id), ct)).ToHttp(http) : Results.Forbid()).RequireAuthorization(Permissions.TenantsRead);

        plans.MapGet("/plan", async (ICurrentUser user, TenantUsageReader reader, HttpContext http, CancellationToken ct) =>
            user.TenantId is { } tenant ? (await reader.ReadAsync(tenant, ct)).ToHttp(http) : Results.NotFound()).RequireAuthorization(Permissions.TenantsRead);
    }

    public static IServiceCollection AddPlanUsage(this IServiceCollection services) => services.AddScoped<TenantUsageReader>();

    private static PlanInput ToInput(PlanBody body) => new(body.Code, body.Name, body.MaxCompanies, body.MaxUsers, body.MaxDocumentsPerMonth, body.IsActive ?? true, body.ResellerId, body.AllowsOverage);

    /// <summary>The plan of a tenant and what it has used, composed from the modules that own each count: Tenancy does not know companies, users or documents.</summary>
    internal sealed class TenantUsageReader(
        IPlanLimits limits, ICompanyAdministration companies, IUserAdministration users, IDocumentService documents, TimeProvider clock)
    {
        public async Task<Result<TenantUsageDto>> ReadAsync(TenantId tenantId, CancellationToken cancellationToken)
        {
            var plan = await limits.OfTenantAsync(tenantId, cancellationToken);
            if (plan is null)
            {
                return Error.NotFound(ErrorCodes.TenantNotFound, "Tenant no encontrado", "El tenant no existe o no es visible para este contexto.");
            }

            var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Lima);
            var from = new DateTimeOffset(local.Year, local.Month, 1, 0, 0, 0, Lima.GetUtcOffset(new DateTime(local.Year, local.Month, 1))).ToUniversalTime();
            var companyCount = await companies.CountAsync(tenantId.Value, cancellationToken);
            var userCount = await users.CountActiveAsync(tenantId.Value, cancellationToken);
            var documentCount = await documents.CountIssuedAsync(tenantId.Value, from, from.AddMonths(1), cancellationToken);
            return new TenantUsageDto(
                plan,
                $"{local.Year:D4}-{local.Month:D2}",
                new UsageItem(companyCount, plan.MaxCompanies),
                new UsageItem(userCount, plan.MaxUsers),
                new UsageItem(documentCount, plan.MaxDocumentsPerMonth));
        }
    }
}
