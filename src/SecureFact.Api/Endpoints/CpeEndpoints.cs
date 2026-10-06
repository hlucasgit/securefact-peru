using SecureFact.CpeEngine.Contracts;
using SecureFact.Identity.Contracts;

namespace SecureFact.Api.Endpoints;

internal static class CpeEndpoints
{
    public static void MapCpeEndpoints(this IEndpointRouteBuilder app)
    {
        // Prepare (generate + sign) the electronic document of a numbered billing document.
        app.MapPost("/api/v1/documents/{documentId:guid}/electronic", async (Guid documentId, IElectronicDocumentService service, HttpContext http, CancellationToken ct) =>
            (await service.PrepareAsync(documentId, ct)).ToHttp(http)).WithTags("CPE").RequireAuthorization(Permissions.CpeSend);

        app.MapGet("/api/v1/documents/{documentId:guid}/electronic", async (Guid documentId, IElectronicDocumentService service, HttpContext http, CancellationToken ct) =>
            (await service.GetByDocumentAsync(documentId, ct)).ToHttp(http)).WithTags("CPE").RequireAuthorization(Permissions.DocumentsRead);

        var group = app.MapGroup("/api/v1/electronic-documents").WithTags("CPE");

        group.MapGet("/{id:guid}", async (Guid id, IElectronicDocumentService service, HttpContext http, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.DocumentsRead);

        group.MapGet("/{id:guid}/events", async (Guid id, IElectronicDocumentService service, HttpContext http, CancellationToken ct) =>
            (await service.ListEventsAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.DocumentsRead);

        group.MapGet("/{id:guid}/xml", async (Guid id, IElectronicDocumentService service, HttpContext http, CancellationToken ct) =>
            (await service.GetSignedXmlAsync(id, ct)).ToHttp(http, xml => Results.Text(xml, "application/xml"))).RequireAuthorization(Permissions.DocumentsRead);

        group.MapGet("/{id:guid}/pdf", async (Guid id, IElectronicDocumentService service, HttpContext http, CancellationToken ct) =>
            (await service.GetPdfAsync(id, ct)).ToHttp(http, pdf => Results.File(pdf, "application/pdf", $"{id:N}.pdf"))).RequireAuthorization(Permissions.DocumentsRead);

        group.MapGet("/{id:guid}/cdr", async (Guid id, IElectronicDocumentService service, HttpContext http, CancellationToken ct) =>
            (await service.GetCdrZipAsync(id, ct)).ToHttp(http, zip => Results.File(zip, "application/zip", $"R-{id:N}.zip"))).RequireAuthorization(Permissions.DocumentsRead);

        // Files kept in object storage, each with a link that works for a few minutes and no credentials: the caller was authorised here, the store only serves the link.
        group.MapGet("/{id:guid}/archive", async (Guid id, IDocumentArchive archive, HttpContext http, CancellationToken ct) =>
            (await archive.ListAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.DocumentsRead);

        group.MapPost("/{id:guid}/send", async (Guid id, IElectronicDocumentService service, HttpContext http, CancellationToken ct) =>
            (await service.SendAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.CpeSend);

        group.MapPost("/{id:guid}/poll", async (Guid id, IElectronicDocumentService service, HttpContext http, CancellationToken ct) =>
            (await service.PollAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.CpeSend);

        group.MapPost("/{id:guid}/recover", async (Guid id, IElectronicDocumentService service, HttpContext http, CancellationToken ct) =>
            (await service.RecoverAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.CpeSend);

        group.MapPost("/{id:guid}/retry", async (Guid id, IElectronicDocumentService service, HttpContext http, CancellationToken ct) =>
            (await service.RetryAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.CpeSend);

        var summaries = app.MapGroup("/api/v1/summaries").WithTags("CPE");

        summaries.MapPost(string.Empty, async (CreateSummaryRequest body, ISummaryService service, HttpContext http, CancellationToken ct) =>
            (await service.CreateAsync(body.CompanyId, body.ReferenceDate, ct)).ToHttp(http, created => Results.Created($"/api/v1/summaries/{created[0].Document.Id}", created)))
            .RequireAuthorization(Permissions.CpeSend);

        summaries.MapGet("/{id:guid}", async (Guid id, ISummaryService service, HttpContext http, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.DocumentsRead);

        var voids = app.MapGroup("/api/v1/voids").WithTags("CPE");

        voids.MapPost(string.Empty, async (CreateVoidRequest body, IVoidService service, HttpContext http, CancellationToken ct) =>
            (await service.CreateAsync(body, ct)).ToHttp(http, created => Results.Created($"/api/v1/voids/{created[0].Document.Id}", created)))
            .RequireAuthorization(Permissions.CpeSend);

        voids.MapGet("/{id:guid}", async (Guid id, IVoidService service, HttpContext http, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.DocumentsRead);
    }
}
