using System.Diagnostics;
using SecureFact.SharedKernel.Tenancy;

namespace SecureFact.Api.Infrastructure;

/// <summary>
/// One structured log line per request with the fields required for operations (trace, correlation, tenant, user, route, duration, result).
/// It deliberately logs no headers, query strings or bodies, so credentials, API keys and personal data cannot leak through here.
/// </summary>
public sealed partial class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            WriteLog(context, started);
        }
    }

    private void WriteLog(HttpContext context, long started)
    {
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var user = context.RequestServices.GetService<ICurrentUser>();
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)";
        var correlation = context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var id) ? id as string : null;
        var tenant = user?.TenantId?.ToString();
        var userId = user?.UserId;
        var trace = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        LogRequest(context.Request.Method, route, context.Response.StatusCode, elapsedMs, trace, correlation, tenant, userId);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "HTTP {Method} {Route} -> {StatusCode} in {DurationMs:0.0} ms [trace {TraceId}, correlation {CorrelationId}, tenant {TenantId}, user {UserId}]")]
    private partial void LogRequest(string method, string route, int statusCode, double durationMs, string traceId, string? correlationId, string? tenantId, Guid? userId);
}
