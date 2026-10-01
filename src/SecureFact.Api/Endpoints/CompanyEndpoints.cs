using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;

namespace SecureFact.Api.Endpoints;

internal static class CompanyEndpoints
{
    public static void MapCompanyEndpoints(this IEndpointRouteBuilder app)
    {
        var companies = app.MapGroup("/api/v1/companies").WithTags("Companies");

        companies.MapGet(string.Empty, async (int? skip, int? take, ICompanyAdministration admin, CancellationToken ct) =>
            Results.Ok(await admin.ListAsync(skip ?? 0, take ?? 50, ct))).RequireAuthorization(Permissions.CompaniesRead);

        companies.MapGet("/{id:guid}", async (Guid id, ICompanyAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.GetAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.CompaniesRead);

        companies.MapPost(string.Empty, async (CreateCompanyRequest body, ICompanyAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.CreateAsync(body, ct)).ToHttp(http, dto => Results.Created($"/api/v1/companies/{dto.Id}", dto)))
            .RequireAuthorization(Permissions.CompaniesManage);

        companies.MapPut("/{id:guid}", async (Guid id, CompanyDetails body, ICompanyAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.UpdateAsync(id, body, ct)).ToHttp(http)).RequireAuthorization(Permissions.CompaniesManage);

        companies.MapPost("/{id:guid}/deactivate", async (Guid id, ICompanyAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.DeactivateAsync(id, ct)).ToNoContent(http)).RequireAuthorization(Permissions.CompaniesManage);

        companies.MapGet("/{id:guid}/establishments", async (Guid id, ICompanyAdministration admin, CancellationToken ct) =>
            Results.Ok(await admin.ListEstablishmentsAsync(id, ct))).RequireAuthorization(Permissions.CompaniesRead);

        companies.MapPost("/{id:guid}/establishments", async (Guid id, CreateEstablishmentRequest body, ICompanyAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.AddEstablishmentAsync(id, body, ct))
                .ToHttp(http, dto => Results.Created($"/api/v1/companies/{id}/establishments/{dto.Id}", dto)))
            .RequireAuthorization(Permissions.CompaniesManage);

        companies.MapPut("/{id:guid}/establishments/{establishmentId:guid}", async (Guid id, Guid establishmentId, EstablishmentDetails body, ICompanyAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.UpdateEstablishmentAsync(id, establishmentId, body, ct)).ToHttp(http)).RequireAuthorization(Permissions.CompaniesManage);

        companies.MapPost("/{id:guid}/establishments/{establishmentId:guid}/deactivate", async (Guid id, Guid establishmentId, ICompanyAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.DeactivateEstablishmentAsync(id, establishmentId, ct)).ToNoContent(http)).RequireAuthorization(Permissions.CompaniesManage);
    }
}
