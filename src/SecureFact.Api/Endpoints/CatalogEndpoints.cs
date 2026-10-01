using SecureFact.Catalogs.Contracts;

namespace SecureFact.Api.Endpoints;

internal static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/catalogs").WithTags("Catalogs");

        // Reference data is the same for every tenant; any authenticated user may read it.
        group.MapGet(string.Empty, async (ICatalogReader reader, CancellationToken ct) =>
            Results.Ok(await reader.ListCatalogsAsync(ct)));

        group.MapGet("/{number}", async (string number, DateOnly? asOf, ICatalogReader reader, HttpContext http, CancellationToken ct) =>
            (await reader.GetEntriesAsync(number, asOf, ct)).ToHttp(http));
    }
}
