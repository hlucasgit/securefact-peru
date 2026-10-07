using System.Security.Cryptography;
using System.Text;
using SecureFact.Api.Infrastructure;
using SecureFact.Identity.Contracts;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Tenancy;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Api.Endpoints;

internal static class DomainEndpoints
{
    public sealed record HostBody(string? Host);

    public static void MapDomainEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").WithTags("Domains");

        // ---------- the reseller sees and checks its own domain ----------

        var own = api.MapGroup("/reseller/domain").RequireAuthorization(Permissions.ResellerBrandingManage);

        own.MapGet(string.Empty, async (ICurrentUser user, IDomains domains, HttpContext http, CancellationToken ct) =>
            user.ResellerId is { } reseller ? (await domains.GetAsync(reseller, ct)).ToHttp(http) : Results.Forbid());

        own.MapPost("/verify", async (ICurrentUser user, IDomains domains, HttpContext http, CancellationToken ct) =>
            user.ResellerId is { } reseller ? (await domains.VerifyAsync(reseller, ct)).ToHttp(http) : Results.Forbid());

        // ---------- platform staff assign the domain of any reseller, and may check it ----------

        api.MapGet("/platform/resellers/{id:guid}/domain", async (Guid id, IDomains domains, HttpContext http, CancellationToken ct) =>
            (await domains.GetAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.TenantsRead);

        api.MapPut("/platform/resellers/{id:guid}/host", async (Guid id, HostBody body, IDomains domains, HttpContext http, CancellationToken ct) =>
            (await domains.SetAsync(id, body.Host, ct)).ToHttp(http)).RequireAuthorization(Permissions.TenantsManage);

        api.MapPost("/platform/resellers/{id:guid}/domain/verify", async (Guid id, IDomains domains, HttpContext http, CancellationToken ct) =>
            (await domains.VerifyAsync(id, ct)).ToHttp(http)).RequireAuthorization(Permissions.TenantsManage);

        // ---------- the edge asks whether a name may have a certificate ----------

        // Caddy (on-demand TLS) calls this with the name before it asks a certificate authority for one. 200 says yes; anything else says no. It answers only to the edge, which knows the
        // secret, and the edge does not forward this route to the outside (deploy/edge/Caddyfile): both are needed, because a certificate for a name that is not a portal of ours costs us
        // the authority's rate limits.
        api.MapGet("/edge/tls-allowed", async (string? domain, string? secret, DomainsOptions options, IDomains domains, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(options.EdgeSecret) || !SecretMatches(secret, options.EdgeSecret))
            {
                return Results.NotFound();
            }

            return !string.IsNullOrWhiteSpace(domain) && await domains.IsAllowedForCertificateAsync(domain, ct) ? Results.Ok() : Results.NotFound();
        }).AllowAnonymous().RequireRateLimiting(RateLimiting.PublicPolicy);
    }

    private static bool SecretMatches(string? given, string expected) =>
        given is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
