using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using SecureFact.Api.Security;
using SecureFact.SharedKernel;

namespace SecureFact.Api.Infrastructure;

internal static class RateLimiting
{
    public const string AuthPolicy = "auth";

    /// <summary>Public reads that a page makes on every visit (the brand of the portal): looser than the credential endpoints.</summary>
    public const string PublicPolicy = "public";

    /// <summary>A short, non-reversible name of what the caller presented (an API key or an access token), or null when it presented nothing.</summary>
    private static string? CredentialOf(HttpContext context)
    {
        var presented = ApiKeyAuthenticationHandler.Presented(context.Request);
        if (presented is null)
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            presented = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization["Bearer ".Length..].Trim() : null;
        }

        return string.IsNullOrEmpty(presented) ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(presented)))[..16];
    }

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
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                await context.HttpContext.Response.WriteAsJsonAsync(problem, token);
            };
            // Every call, whoever makes it (ADR-066): a limit per credential (the key or the token presented, by its hash) and another, wider, per address, so inventing credentials does not escape it.
            // The health checks are never limited. Both are per minute and configurable.
            var perCredential = configuration.GetValue("RateLimiting:ApiPermitPerMinute", 600);
            var perAddress = configuration.GetValue("RateLimiting:AddressPermitPerMinute", 3000);
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(context => context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal)
                    ? RateLimitPartition.GetNoLimiter("health")
                    : RateLimitPartition.GetFixedWindowLimiter($"ip:{context.Connection.RemoteIpAddress}", _ => new FixedWindowRateLimiterOptions { PermitLimit = perAddress, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })),
                PartitionedRateLimiter.Create<HttpContext, string>(context => !context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal) && CredentialOf(context) is { } credential
                    ? RateLimitPartition.GetFixedWindowLimiter($"credential:{credential}", _ => new FixedWindowRateLimiterOptions { PermitLimit = perCredential, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("anonymous")));
            options.AddPolicy(AuthPolicy, httpContext => RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = configuration.GetValue("RateLimiting:AuthPermitPerMinute", 20), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy(PublicPolicy, httpContext => RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = configuration.GetValue("RateLimiting:PublicPermitPerMinute", 240), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
}
