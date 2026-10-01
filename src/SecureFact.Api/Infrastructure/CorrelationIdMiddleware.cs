using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SecureFact.Api.Infrastructure;

/// <summary>
/// Accepts a caller-supplied <c>X-Correlation-Id</c> only when it is a short, safe token; otherwise generates one.
/// The value is echoed in the response, attached to the log scope and to the current activity.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "SecureFact.CorrelationId";

    [GeneratedRegex("^[A-Za-z0-9._-]{8,64}$")]
    private static partial Regex SafeToken();

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName].ToString();
        var correlationId = SafeToken().IsMatch(supplied) ? supplied : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("securefact.correlation_id", correlationId);

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["TraceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
        }))
        {
            await next(context);
        }
    }
}
