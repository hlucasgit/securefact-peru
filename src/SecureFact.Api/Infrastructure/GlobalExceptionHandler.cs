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
