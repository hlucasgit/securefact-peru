using Microsoft.AspNetCore.Mvc;
using SecureFact.Api.Infrastructure;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Api.Endpoints;

internal static class ResultHttpExtensions
{
    private static readonly HashSet<string> Unauthenticated = new(StringComparer.Ordinal)
    {
        ErrorCodes.InvalidCredentials, ErrorCodes.MfaRequired, ErrorCodes.InvalidMfaCode, ErrorCodes.InvalidRefreshToken,
    };

    public static IResult ToHttp<T>(this Result<T> result, HttpContext context, Func<T, IResult>? success = null) =>
        result.IsSuccess ? (success?.Invoke(result.Value) ?? Results.Ok(result.Value)) : ToProblem(result.Error, context);

    public static IResult ToNoContent(this Result<Unit> result, HttpContext context) =>
        result.IsSuccess ? Results.NoContent() : ToProblem(result.Error, context);

    public static IResult ToProblem(Error error, HttpContext context)
    {
        var status = Unauthenticated.Contains(error.Code)
            ? StatusCodes.Status401Unauthorized
            : error.Kind switch
            {
                ErrorKind.Validation => StatusCodes.Status422UnprocessableEntity,
                ErrorKind.NotFound => StatusCodes.Status404NotFound,
                ErrorKind.Conflict => StatusCodes.Status409Conflict,
                ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
                ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status500InternalServerError,
            };

        var problem = new ProblemDetails
        {
            Type = $"https://docs.securefact.pe/errors/{error.Code}",
            Title = error.Title,
            Detail = error.Detail,
            Status = status,
        };
        ProblemDetailsExtensions.Enrich(problem, context, error.Code);
        return Results.Json(problem, statusCode: status, contentType: "application/problem+json");
    }
}
