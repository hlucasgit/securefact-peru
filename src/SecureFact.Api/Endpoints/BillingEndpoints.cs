using SecureFact.Billing.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.SharedKernel;

namespace SecureFact.Api.Endpoints;

internal static class BillingEndpoints
{
    public const string IdempotencyHeader = "Idempotency-Key";

    public static void MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        var series = app.MapGroup("/api/v1/series").WithTags("Series");

        series.MapGet(string.Empty, async (Guid companyId, ISeriesAdministration admin, CancellationToken ct) =>
            Results.Ok(await admin.ListAsync(companyId, ct))).RequireAuthorization(Permissions.DocumentsRead);

        series.MapPost(string.Empty, async (CreateSeriesRequest body, ISeriesAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.CreateAsync(body, ct)).ToHttp(http, dto => Results.Created($"/api/v1/series/{dto.Id}", dto)))
            .RequireAuthorization(Permissions.SeriesManage);

        series.MapPost("/{id:guid}/deactivate", async (Guid id, ISeriesAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.DeactivateAsync(id, ct)).ToNoContent(http)).RequireAuthorization(Permissions.SeriesManage);

        var documents = app.MapGroup("/api/v1/documents").WithTags("Documents");

        documents.MapPost(string.Empty, async (CreateDocumentRequest body, IDocumentService service, HttpContext http, CancellationToken ct) =>
        {
            var key = http.Request.Headers[IdempotencyHeader].ToString();
            var result = await service.CreateAsync(key, body, ct);
            return result.ToHttp(http, dto => Results.Created($"/api/v1/documents/{dto.Id}", dto));
        }).RequireAuthorization(Permissions.DocumentsCreate);

        documents.MapPost("/preview", async (PreviewRequest body, IDocumentService service, HttpContext http, CancellationToken ct) =>
            (await service.PreviewAsync(body, ct)).ToHttp(http)).RequireAuthorization(Permissions.DocumentsCreate);

        app.MapPost("/api/v1/notes/preview", async (NotePreviewRequest body, IDocumentService service, HttpContext http, CancellationToken ct) =>
            (await service.PreviewNoteAsync(body, ct)).ToHttp(http)).RequireAuthorization(Permissions.DocumentsCreate);

        // Credit and debit notes: the series (07 or 08) decides which; same idempotency and numbering guarantees as documents.
        app.MapPost("/api/v1/notes", async (CreateNoteRequest body, IDocumentService service, HttpContext http, CancellationToken ct) =>
        {
            var key = http.Request.Headers[IdempotencyHeader].ToString();
            var result = await service.CreateNoteAsync(key, body, ct);
            return result.ToHttp(http, dto => Results.Created($"/api/v1/documents/{dto.Id}", dto));
        }).WithTags("Documents").RequireAuthorization(Permissions.DocumentsCreate);

        documents.MapGet("/{id:guid}", async (Guid id, IDocumentService service, HttpContext http, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.DocumentsRead);

        documents.MapGet(string.Empty, async (Guid? companyId, int? skip, int? take, IDocumentService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(companyId, skip ?? 0, take ?? 50, ct))).RequireAuthorization(Permissions.DocumentsRead);
    }
}
