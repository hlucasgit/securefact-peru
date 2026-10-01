using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SecureFact.SharedKernel;

namespace SecureFact.Api.Infrastructure;

/// <summary>Turns unhandled exceptions into RFC 9457 problem details without leaking internals.</summary>
public sealed partial class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return true;
        }

        if (exception is BadHttpRequestException badRequest)
        {
            var invalid = new ProblemDetails
            {
                Type = "https://docs.securefact.pe/errors/SF-VAL-005",
                Title = "Solicitud inválida",
                Status = StatusCodes.Status400BadRequest,
                Detail = "El cuerpo o los parámetros de la solicitud no son válidos.",
            };
            ProblemDetailsExtensions.Enrich(invalid, httpContext, ErrorCodes.InvalidRequest);
            httpContext.Response.StatusCode = badRequest.StatusCode;
            await httpContext.Response.WriteAsJsonAsync(invalid, cancellationToken);
            return true;
        }

        LogUnhandled(exception, httpContext.Request.Method, httpContext.Request.Path);

        var problem = new ProblemDetails
        {
            Type = "https://docs.securefact.pe/errors/SF-SYS-001",
            Title = "Error interno",
            Status = StatusCodes.Status500InternalServerError,
            Detail = "Ocurrió un error inesperado. Indique el traceId al contactar soporte.",
        };
        ProblemDetailsExtensions.Enrich(problem, httpContext, ErrorCodes.Unexpected);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception processing {Method} {Path}")]
    private partial void LogUnhandled(Exception exception, string method, PathString path);
}
