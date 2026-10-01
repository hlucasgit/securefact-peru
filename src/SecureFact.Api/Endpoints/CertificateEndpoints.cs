using SecureFact.Certificates.Contracts;
using SecureFact.Identity.Contracts;

namespace SecureFact.Api.Endpoints;

internal static class CertificateEndpoints
{
    public static void MapCertificateEndpoints(this IEndpointRouteBuilder app)
    {
        var certificates = app.MapGroup("/api/v1/certificates").WithTags("Certificates");

        certificates.MapPost(string.Empty, async (UploadCertificateRequest body, ICertificateAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.UploadAsync(body, ct)).ToHttp(http, dto => Results.Created($"/api/v1/certificates/{dto.Id}", dto)))
            .RequireAuthorization(Permissions.CertificatesManage);

        certificates.MapGet(string.Empty, async (Guid companyId, ICertificateAdministration admin, CancellationToken ct) =>
            Results.Ok(await admin.ListAsync(companyId, ct))).RequireAuthorization(Permissions.CertificatesRead);

        certificates.MapGet("/expiring", async (int? days, ICertificateAdministration admin, CancellationToken ct) =>
            Results.Ok(await admin.ListExpiringAsync(days ?? 30, ct))).RequireAuthorization(Permissions.CertificatesRead);

        certificates.MapPost("/{id:guid}/deactivate", async (Guid id, ICertificateAdministration admin, HttpContext http, CancellationToken ct) =>
            (await admin.DeactivateAsync(id, ct)).ToNoContent(http)).RequireAuthorization(Permissions.CertificatesManage);
    }
}
