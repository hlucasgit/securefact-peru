using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SecureFact.Identity;
using SecureFact.Identity.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Domain;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Api.Security;

/// <summary>
/// Authenticates a call made with an API key (ADR-066): <c>Authorization: Bearer sfk_…</c>, or the key alone in <c>X-Api-Key</c>. The result is a principal of the tenant of the key with the role of the key and
/// nothing else: no session, no way to reach another tenant, and none of the roles that manage the tenant. The data scope comes only from the key, as it does from the token of a person.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    public const string ApiKeyHeader = "X-Api-Key";

    /// <summary>The claim that says how the caller proved who it is, so the audit trail and the logs can tell a program from a person.</summary>
    public const string MethodClaim = "amr";

    /// <summary>The key a request presents, in either of the two headers; null when it presents none.</summary>
    public static string? Presented(HttpRequest request)
    {
        if (request.Headers.TryGetValue(ApiKeyHeader, out var header) && header.ToString().Trim() is { Length: > 0 } direct)
        {
            return direct;
        }

        var authorization = request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        return authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase) && authorization[bearer.Length..].Trim() is { } token && token.StartsWith(IApiKeys.Prefix, StringComparison.Ordinal)
            ? token
            : null;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Presented(Request) is not { } presented)
        {
            return AuthenticateResult.NoResult();
        }

        var services = Context.RequestServices;
        var identity = await services.GetRequiredService<IApiKeys>().AuthenticateAsync(presented, Context.RequestAborted);
        if (identity is null)
        {
            return AuthenticateResult.Fail("The API key is not valid.");
        }

        // The scope comes first: the registry of tenants shows a caller only its own row, so the status of the tenant is read inside its scope. A suspended or closed tenant is refused on every request,
        // as it is for a person.
        var tenantId = new TenantId(identity.TenantId);
        services.GetRequiredService<DataScope>().UseTenant(tenantId);
        if (await services.GetRequiredService<ITenantStatusReader>().GetStatusAsync(tenantId, fresh: false, Context.RequestAborted) != TenantStatus.Active)
        {
            return AuthenticateResult.Fail("The tenant is not active.");
        }

        var claims = new[]
        {
            new Claim("sub", identity.KeyId.ToString("D")),
            new Claim(IdentityModule.TenantClaim, identity.TenantId.ToString("D")),
            new Claim(IdentityModule.RoleClaim, identity.Role),
            new Claim(MethodClaim, "api_key"),
        };
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }
}
