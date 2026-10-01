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

        group.MapGet("/{id:guid}/cdr", async (Guid id, IElectronicDocumentService service, HttpContext http, CancellationToken ct) =>
            (await service.GetCdrZipAsync(id, ct)).ToHttp(http, zip => Results.File(zip, "application/zip", $"R-{id:N}.zip"))).RequireAuthorization(Permissions.DocumentsRead);

        group.MapPost("/{id:guid}/send", async (Guid id, IElectronicDocumentService service, HttpContext http, CancellationToken ct) =>
            (await service.SendAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.CpeSend);

        group.MapPost("/{id:guid}/retry", async (Guid id, IElectronicDocumentService service, HttpContext http, CancellationToken ct) =>
            (await service.RetryAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.CpeSend);
    }
}
