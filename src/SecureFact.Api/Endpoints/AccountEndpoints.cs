using SecureFact.Api.Infrastructure;
using SecureFact.Identity.Contracts;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Api.Endpoints;

internal static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/account").WithTags("Account");

        // The account takes everything it owns in one ZIP (ADR-074). The package is built on a temporary file, so a large account does not fill the memory of the server, and the file is deleted when the
        // response ends. A session of support never gets it (it holds no permission to), and it is limited by credential because it is the heaviest read of the API.
        group.MapGet("/export", async (IAccountExport export, HttpContext http, CancellationToken ct) =>
            (await export.PrepareAsync(ct)).ToHttp(http, package => Results.File(package.Content, "application/zip", package.FileName)))
            .RequireAuthorization(Permissions.AccountExport).RequireRateLimiting(RateLimiting.ExportPolicy);
    }
}
