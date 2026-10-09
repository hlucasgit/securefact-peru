using SecureFact.Gre.Contracts;
using SecureFact.Identity.Contracts;

namespace SecureFact.Api.Endpoints;

internal static class GreEndpoints
{
    public static void MapGreEndpoints(this IEndpointRouteBuilder app)
    {
        var series = app.MapGroup("/api/v1/gre/series").WithTags("GRE");

        series.MapGet(string.Empty, async (Guid companyId, IGreSeriesAdministration admin, CancellationToken ct) =>
            Results.Ok(await admin.ListAsync(companyId, ct))).RequireAuthorization(Permissions.DocumentsRead);

        series.MapPost(string.Empty, async (CreateGreSeriesBody body, IGreSeriesAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.CreateAsync(body.CompanyId, body.Code, ct)).ToHttp(http, dto => Results.Created($"/api/v1/gre/series/{dto.Id}", dto)))
            .RequireAuthorization(Permissions.SeriesManage);

        series.MapPost("/{id:guid}/deactivate", async (Guid id, IGreSeriesAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.DeactivateAsync(id, ct)).ToNoContent(http)).RequireAuthorization(Permissions.SeriesManage);

        var guides = app.MapGroup("/api/v1/gre/guides").WithTags("GRE");

        guides.MapGet(string.Empty, async (Guid? companyId, GreState? state, int? skip, int? take, IGreService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(companyId, state, skip ?? 0, take ?? 25, ct))).RequireAuthorization(Permissions.DocumentsRead);

        guides.MapPost(string.Empty, async (CreateGreRequest body, IGreService service, HttpContext http, CancellationToken ct) =>
            (await service.CreateAsync(body, ct)).ToHttp(http, dto => Results.Created($"/api/v1/gre/guides/{dto.Id}", dto))).RequireAuthorization(Permissions.CpeSend);

        guides.MapPost("/carrier", async (CreateGreCarrierRequest body, IGreService service, HttpContext http, CancellationToken ct) =>
            (await service.CreateCarrierAsync(body, ct)).ToHttp(http, dto => Results.Created($"/api/v1/gre/guides/{dto.Id}", dto))).RequireAuthorization(Permissions.CpeSend);

        guides.MapGet("/{id:guid}", async (Guid id, IGreService service, HttpContext http, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.DocumentsRead);

        guides.MapPost("/{id:guid}/submit", async (Guid id, IGreService service, HttpContext http, CancellationToken ct) =>
            (await service.SubmitAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.CpeSend);

        guides.MapPost("/{id:guid}/refresh", async (Guid id, IGreService service, HttpContext http, CancellationToken ct) =>
            (await service.RefreshAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.CpeSend);

        guides.MapGet("/{id:guid}/xml", async (Guid id, IGreService service, HttpContext http, CancellationToken ct) =>
            (await service.GetXmlAsync(id, ct)).ToHttp(http, xml => Results.Text(xml, "application/xml"))).RequireAuthorization(Permissions.DocumentsRead);

        guides.MapGet("/{id:guid}/pdf", async (Guid id, IGreService service, HttpContext http, CancellationToken ct) =>
            (await service.GetPdfAsync(id, ct)).ToHttp(http, pdf => Results.File(pdf, "application/pdf", $"{id:N}.pdf"))).RequireAuthorization(Permissions.DocumentsRead);

        guides.MapGet("/{id:guid}/cdr", async (Guid id, IGreService service, HttpContext http, CancellationToken ct) =>
            (await service.GetCdrAsync(id, ct)).ToHttp(http, zip => Results.File(zip, "application/zip", $"R-{id:N}.zip"))).RequireAuthorization(Permissions.DocumentsRead);
    }

    private sealed record CreateGreSeriesBody(Guid CompanyId, string Code);
}
