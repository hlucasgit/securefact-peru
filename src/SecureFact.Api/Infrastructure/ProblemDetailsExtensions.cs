using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace SecureFact.Api.Infrastructure;

public static class ProblemDetailsExtensions
{
    /// <summary>Adds the stable error code, trace and correlation identifiers required on every error response.</summary>
    public static void Enrich(ProblemDetails problem, HttpContext context, string code)
    {
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        if (context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var correlationId))
        {
            problem.Extensions["correlationId"] = correlationId;
        }
    }
}
