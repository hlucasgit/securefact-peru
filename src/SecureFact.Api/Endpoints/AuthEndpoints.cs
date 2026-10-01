using SecureFact.Api.Infrastructure;
using SecureFact.Identity.Contracts;
using SecureFact.SharedKernel.Tenancy;

namespace SecureFact.Api.Endpoints;

internal static class AuthEndpoints
{
    public sealed record RefreshBody(string RefreshToken);

    public sealed record TotpBody(string Code);

    public sealed record ResetRequestBody(string Email);

    public sealed record ResetConfirmBody(string Token, string NewPassword);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");
        var anonymous = group.MapGroup(string.Empty).AllowAnonymous().RequireRateLimiting(RateLimiting.AuthPolicy);

        anonymous.MapPost("/login", async (LoginRequest body, IAuthenticationService auth, HttpContext http, CancellationToken ct) =>
            (await auth.LoginAsync(body, ClientOf(http), ct)).ToHttp(http));

        anonymous.MapPost("/refresh", async (RefreshBody body, IAuthenticationService auth, HttpContext http, CancellationToken ct) =>
            (await auth.RefreshAsync(body.RefreshToken, ClientOf(http), ct)).ToHttp(http));

        anonymous.MapPost("/password-reset/request", async (ResetRequestBody body, IPasswordResetService reset, HttpContext http, CancellationToken ct) =>
            (await reset.RequestAsync(body.Email, ct)).ToNoContent(http));

        anonymous.MapPost("/password-reset/confirm", async (ResetConfirmBody body, IPasswordResetService reset, HttpContext http, CancellationToken ct) =>
            (await reset.ConfirmAsync(body.Token, body.NewPassword, ct)).ToNoContent(http));

        group.MapPost("/logout", async (IAuthenticationService auth, ICurrentUser user, HttpContext http, CancellationToken ct) =>
            user.SessionId is { } sid ? (await auth.LogoutAsync(sid, ct)).ToNoContent(http) : Results.Unauthorized());

        group.MapPost("/mfa/enroll", async (IAuthenticationService auth, ICurrentUser user, HttpContext http, CancellationToken ct) =>
            user.UserId is { } id ? (await auth.BeginMfaEnrollmentAsync(id, ct)).ToHttp(http) : Results.Unauthorized());

        group.MapPost("/mfa/confirm", async (TotpBody body, IAuthenticationService auth, ICurrentUser user, HttpContext http, CancellationToken ct) =>
            user.UserId is { } id ? (await auth.ConfirmMfaEnrollmentAsync(id, body.Code, ct)).ToNoContent(http) : Results.Unauthorized());
    }

    private static ClientInfo ClientOf(HttpContext http) =>
        new(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());
}
