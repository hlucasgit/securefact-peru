using SecureFact.Api.Infrastructure;
using SecureFact.Identity.Contracts;
using SecureFact.SharedKernel.Tenancy;

namespace SecureFact.Api.Endpoints;

internal static class AuthEndpoints
{
    public sealed record RefreshBody(string RefreshToken);

    /// <summary>
    /// A browser asks to keep its refresh token out of reach of the page (ADR-050): with this header the server puts it in an HttpOnly cookie and does not return it in the body. The header
    /// is not one a form or a cross-site request can send, so it also stands for a deliberate call of our own page; clients of the API that do not send it keep the token in the body.
    /// </summary>
    public const string SessionHeader = "X-SecureFact-Session";

    public const string SessionCookieMode = "cookie";

    /// <summary>Name of the cookie. Its path is the auth routes only, so the browser sends it nowhere else.</summary>
    public const string CookieName = "sf_rt";

    private const string CookiePath = "/api/v1/auth";

    public sealed record TotpBody(string Code);

    public sealed record ResetRequestBody(string Email);

    public sealed record ResetConfirmBody(string Token, string NewPassword);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");
        var anonymous = group.MapGroup(string.Empty).AllowAnonymous().RequireRateLimiting(RateLimiting.AuthPolicy);

        anonymous.MapPost("/login", async (LoginRequest body, IAuthenticationService auth, HttpContext http, CancellationToken ct) =>
            InCookieMode(await auth.LoginAsync(body, ClientOf(http), ct), http));

        // The refresh token comes from the cookie when the page asks for the cookie mode, and from the body otherwise. A cookie without the header is not used: a request that the page did
        // not make cannot spend it.
        anonymous.MapPost("/refresh", async (RefreshBody? body, IAuthenticationService auth, HttpContext http, CancellationToken ct) =>
            InCookieMode(await auth.RefreshAsync(CookieMode(http) ? http.Request.Cookies[CookieName] ?? string.Empty : body?.RefreshToken ?? string.Empty, ClientOf(http), ct), http));

        anonymous.MapPost("/password-reset/request", async (ResetRequestBody body, IPasswordResetService reset, HttpContext http, CancellationToken ct) =>
            (await reset.RequestAsync(body.Email, ct)).ToNoContent(http));

        anonymous.MapPost("/password-reset/confirm", async (ResetConfirmBody body, IPasswordResetService reset, HttpContext http, CancellationToken ct) =>
            (await reset.ConfirmAsync(body.Token, body.NewPassword, ct)).ToNoContent(http));

        group.MapPost("/logout", async (IAuthenticationService auth, ICurrentUser user, HttpContext http, CancellationToken ct) =>
        {
            ClearCookie(http);
            return user.SessionId is { } sid ? (await auth.LogoutAsync(sid, ct)).ToNoContent(http) : Results.Unauthorized();
        });

        group.MapPost("/mfa/enroll", async (IAuthenticationService auth, ICurrentUser user, HttpContext http, CancellationToken ct) =>
            user.UserId is { } id ? (await auth.BeginMfaEnrollmentAsync(id, ct)).ToHttp(http) : Results.Unauthorized());

        group.MapPost("/mfa/confirm", async (TotpBody body, IAuthenticationService auth, ICurrentUser user, HttpContext http, CancellationToken ct) =>
            user.UserId is { } id ? (await auth.ConfirmMfaEnrollmentAsync(id, body.Code, ct)).ToNoContent(http) : Results.Unauthorized());
    }

    private static bool CookieMode(HttpContext http) => http.Request.Headers[SessionHeader].ToString() == SessionCookieMode;

    /// <summary>
    /// In the cookie mode the refresh token travels in an HttpOnly, SameSite=Strict cookie that is a session cookie (it goes with the browser session, as the storage of the tab did) and is
    /// Secure everywhere but a development server on plain HTTP; the body carries the access token only. A refusal clears the cookie. Otherwise the answer is the usual one.
    /// </summary>
    private static IResult InCookieMode(SecureFact.SharedKernel.Results.Result<AuthTokens> result, HttpContext http)
    {
        if (!CookieMode(http))
        {
            return result.ToHttp(http);
        }

        if (!result.IsSuccess)
        {
            ClearCookie(http);
            return result.ToHttp(http);
        }

        var secure = http.Request.IsHttps || !http.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment();
        http.Response.Cookies.Append(CookieName, result.Value.RefreshToken, new CookieOptions { HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Strict, Path = CookiePath, IsEssential = true });
        return Results.Ok(result.Value with { RefreshToken = string.Empty });
    }

    private static void ClearCookie(HttpContext http) =>
        http.Response.Cookies.Delete(CookieName, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = CookiePath, Secure = http.Request.IsHttps || !http.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment() });

    private static ClientInfo ClientOf(HttpContext http) =>
        new(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());
}
