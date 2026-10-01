using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SecureFact.Api.Infrastructure;
using SecureFact.SharedKernel;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = ctx =>
    {
        if (!ctx.ProblemDetails.Extensions.ContainsKey("code"))
        {
            var code = ctx.HttpContext.Response.StatusCode switch
            {
                StatusCodes.Status401Unauthorized => ErrorCodes.Unauthenticated,
                StatusCodes.Status403Forbidden => ErrorCodes.Forbidden,
                StatusCodes.Status429TooManyRequests => ErrorCodes.RateLimited,
                >= 400 and < 500 => ErrorCodes.InvalidRequest,
                _ => ErrorCodes.Unexpected,
            };
            ProblemDetailsExtensions.Enrich(ctx.ProblemDetails, ctx.HttpContext, code);
        }
    });
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = c => c.Tags.Contains("live") });
app.MapHealthChecks("/health/ready");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();

/// <summary>Entry point marker so integration tests can host the API.</summary>
public partial class Program;
