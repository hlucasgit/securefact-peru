using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using SecureFact.SharedKernel;

namespace SecureFact.Api.Infrastructure;

internal static class RateLimiting
{
    public const string AuthPolicy = "auth";

    /// <summary>Public reads that a page makes on every visit (the brand of the portal): looser than the credential endpoints.</summary>
    public const string PublicPolicy = "public";

    /// <summary>Per-client-IP limiter for unauthenticated credential endpoints; account lockout complements it per account.</summary>
    public static IServiceCollection AddSecureFactRateLimiting(this IServiceCollection services, IConfiguration configuration) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, token) =>
            {
                var problem = new ProblemDetails
                {
                    Type = $"https://docs.securefact.pe/errors/{ErrorCodes.RateLimited}",
                    Title = "Demasiadas solicitudes",
                    Status = StatusCodes.Status429TooManyRequests,
                    Detail = "Reintente más tarde.",
                };
                ProblemDetailsExtensions.Enrich(problem, context.HttpContext, ErrorCodes.RateLimited);
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsJsonAsync(problem, token);
            };
            options.AddPolicy(AuthPolicy, httpContext => RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = configuration.GetValue("RateLimiting:AuthPermitPerMinute", 20), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy(PublicPolicy, httpContext => RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = configuration.GetValue("RateLimiting:PublicPermitPerMinute", 240), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
}
