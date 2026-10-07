using SecureFact.Api.Infrastructure;
using SecureFact.Identity.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Api.Endpoints;

internal static class BrandingEndpoints
{
    /// <summary>What anyone may see of a brand. Nothing here is secret: it is what the sign-in page shows.</summary>
    public sealed record BrandingResponse(string BrandName, string PrimaryColor, string? SupportEmail, string? LogoUrl);

    public sealed record SettingsResponse(Guid ResellerId, string ResellerName, string? BrandName, string? PrimaryColor, string? SupportEmail, string? Host, DomainStatus HostStatus, string? LogoUrl);

    public sealed record BrandBody(string? BrandName, string? PrimaryColor, string? SupportEmail);

    public sealed record LogoBody(string DataBase64);

    private static string? LogoUrl(Guid resellerId, string? version) => version is null ? null : $"/api/v1/branding/logos/{resellerId}?v={version}";

    private static BrandingResponse? ToResponse(BrandingDto? brand) => brand is null ? null : new(brand.BrandName, brand.PrimaryColor, brand.SupportEmail, LogoUrl(brand.ResellerId, brand.LogoVersion));

    private static SettingsResponse ToResponse(BrandingSettings s) => new(s.ResellerId, s.ResellerName, s.BrandName, s.PrimaryColor, s.SupportEmail, s.Host, s.HostStatus, LogoUrl(s.ResellerId, s.LogoVersion));

    private static IResult Brand(BrandingDto? brand) => brand is null ? Results.NoContent() : Results.Ok(ToResponse(brand));

    private static IResult ToLogoResult(Result<BrandingSettings> result, HttpContext http) => result.ToHttp(http, settings => Results.Ok(ToResponse(settings)));

    private static Result<byte[]> Decode(string? base64) =>
        base64 is { Length: > 0 and <= 400_000 } && Convert.TryFromBase64String(base64, new byte[base64.Length], out _)
            ? Convert.FromBase64String(base64)
            : Error.Validation(ErrorCodes.InvalidLogo, "Logotipo inválido", "Envíe el logotipo en base64, de hasta 200 KB.");

    public static void MapBrandingEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").WithTags("Branding");

        // ---------- public: the sign-in page asks before anyone has signed in ----------

        var open = api.MapGroup("/branding").AllowAnonymous().RequireRateLimiting(RateLimiting.PublicPolicy);

        open.MapGet(string.Empty, async (string? host, IBranding branding, CancellationToken ct) =>
            Brand(string.IsNullOrWhiteSpace(host) ? null : await branding.ForHostAsync(host, ct)));

        open.MapGet("/logos/{resellerId:guid}", async (Guid resellerId, IBranding branding, HttpContext http, CancellationToken ct) =>
        {
            if (await branding.LogoAsync(resellerId, ct) is not { } logo)
            {
                return Results.NotFound();
            }

            // The version is part of the address the page uses, so a cached logo is never shown after a new one is uploaded.
            http.Response.Headers.CacheControl = "public, max-age=300";
            http.Response.Headers.ETag = $"\"{logo.Version}\"";
            return Results.File(logo.Data, logo.ContentType);
        });

        // ---------- the brand of whoever is signed in ----------

        api.MapGet("/branding/current", async (ICurrentUser user, IBranding branding, CancellationToken ct) =>
            Brand(user.ResellerId is { } reseller
                ? await branding.ForResellerAsync(reseller, ct)
                : user.TenantId is { } tenant && !user.IsPlatform ? await branding.ForTenantAsync(tenant, ct) : null));

        // ---------- the reseller edits its own brand ----------

        var own = api.MapGroup("/reseller/branding").RequireAuthorization(Permissions.ResellerBrandingManage);

        own.MapGet(string.Empty, async (ICurrentUser user, IBranding branding, HttpContext http, CancellationToken ct) =>
            user.ResellerId is { } reseller ? ToLogoResult(await branding.GetAsync(reseller, ct), http) : Results.Forbid());

        own.MapPut(string.Empty, async (BrandBody body, ICurrentUser user, IBranding branding, HttpContext http, CancellationToken ct) =>
            user.ResellerId is { } reseller ? ToLogoResult(await branding.UpdateAsync(reseller, new BrandingInput(body.BrandName, body.PrimaryColor, body.SupportEmail), ct), http) : Results.Forbid());

        own.MapPut("/logo", async (LogoBody body, ICurrentUser user, IBranding branding, HttpContext http, CancellationToken ct) =>
        {
            if (user.ResellerId is not { } reseller)
            {
                return Results.Forbid();
            }

            var data = Decode(body.DataBase64);
            return data.IsSuccess ? ToLogoResult(await branding.SetLogoAsync(reseller, data.Value, ct), http) : data.ToHttp(http);
        });

        own.MapDelete("/logo", async (ICurrentUser user, IBranding branding, HttpContext http, CancellationToken ct) =>
            user.ResellerId is { } reseller ? ToLogoResult(await branding.RemoveLogoAsync(reseller, ct), http) : Results.Forbid());

        // ---------- platform staff edit any reseller, and alone assign the domain ----------

        api.MapGet("/platform/resellers/{id:guid}/branding", async (Guid id, IBranding branding, HttpContext http, CancellationToken ct) =>
            ToLogoResult(await branding.GetAsync(id, ct), http)).RequireAuthorization(Permissions.TenantsRead);

        api.MapPut("/platform/resellers/{id:guid}/branding", async (Guid id, BrandBody body, IBranding branding, HttpContext http, CancellationToken ct) =>
            ToLogoResult(await branding.UpdateAsync(id, new BrandingInput(body.BrandName, body.PrimaryColor, body.SupportEmail), ct), http)).RequireAuthorization(Permissions.TenantsManage);

        api.MapPut("/platform/resellers/{id:guid}/branding/logo", async (Guid id, LogoBody body, IBranding branding, HttpContext http, CancellationToken ct) =>
        {
            var data = Decode(body.DataBase64);
            return data.IsSuccess ? ToLogoResult(await branding.SetLogoAsync(id, data.Value, ct), http) : data.ToHttp(http);
        }).RequireAuthorization(Permissions.TenantsManage);

        api.MapDelete("/platform/resellers/{id:guid}/branding/logo", async (Guid id, IBranding branding, HttpContext http, CancellationToken ct) =>
            ToLogoResult(await branding.RemoveLogoAsync(id, ct), http)).RequireAuthorization(Permissions.TenantsManage);
    }
}
