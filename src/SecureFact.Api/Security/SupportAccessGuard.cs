using SecureFact.Api.Endpoints;
using SecureFact.Identity;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Api.Security;

/// <summary>
/// The person of the service provider who entered an account (ADR-069) only reads. The role of the token has no permission to change anything; this is the second lock, in front of every endpoint
/// (also the ones that ask for no permission, such as the ones about the own session or the own second factor): a request that is not a read is refused, whatever it is. The one exception is
/// ending the session.
/// </summary>
internal sealed class SupportAccessGuard(RequestDelegate next)
{
    private const string LogoutPath = "/api/v1/auth/logout";

    private static readonly Error ReadOnly = Error.Forbidden(
        ErrorCodes.SupportReadOnly, "Acceso de solo lectura", "Quien entra como soporte solo puede ver la cuenta: no cambia nada. Salga del modo soporte para actuar con su propia cuenta.");

    public Task InvokeAsync(HttpContext context)
    {
        var supporting = context.User.Identity?.IsAuthenticated == true && context.User.FindFirst(IdentityModule.SupportClaim) is not null;
        if (!supporting || HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method)
            || (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.Equals(LogoutPath, StringComparison.Ordinal)))
        {
            return next(context);
        }

        return ResultHttpExtensions.ToProblem(ReadOnly, context).ExecuteAsync(context);
    }
}
