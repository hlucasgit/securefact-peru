using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SecureFact.Api.Endpoints;
using SecureFact.Api.Infrastructure;
using SecureFact.Api.Security;
using SecureFact.Audit;
using SecureFact.Billing;
using SecureFact.Identity;
using SecureFact.Organizations;
using SecureFact.Identity.Contracts;
using SecureFact.Platform;
using SecureFact.Platform.Security;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Telemetry;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.TaxEngine;
using SecureFact.Tenancy;

var builder = WebApplication.CreateBuilder(args);

var appConnection = builder.Configuration.GetConnectionString("App");
var migrationsConnection = builder.Configuration.GetConnectionString("Migrations");

if (!builder.Environment.IsDevelopment())
{
    builder.Logging.AddJsonConsole();
}

var telemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("securefact-api"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation(options => options.Filter = http => !http.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal))
        .AddHttpClientInstrumentation()
        .AddSource("Npgsql"))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter(SecureFactTelemetry.MeterName));
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    telemetry.UseOtlpExporter();
}

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
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

builder.Services.AddPlatformDataScope();
builder.Services.AddSingleton<ISecretProtector>(_ =>
{
    // ADR-007: the in-process KEK is for development and tests only. Production needs a KMS/Vault-backed protector.
    if (builder.Environment.IsProduction())
    {
        throw new InvalidOperationException("No production ISecretProtector is configured. Register a KMS/Vault-backed implementation (ADR-007).");
    }

    return LocalEnvelopeSecretProtector.FromBase64(builder.Configuration["Security:LocalDevKek"]);
});
builder.Services.AddScoped<IPasswordResetNotifier, UnconfiguredPasswordResetNotifier>();
builder.Services.AddSecureFactRateLimiting(builder.Configuration);
builder.Services.AddScoped<IRequestContext, HttpRequestContext>();

if (appConnection is not null)
{
    builder.Services.AddAuditModule(appConnection);
    builder.Services.AddTenancyModule(appConnection);
    builder.Services.AddOrganizationsModule(appConnection);
    builder.Services.AddTaxEngineModule();
    builder.Services.AddBillingModule(appConnection);
    builder.Services.AddIdentityModule(appConnection, options => builder.Configuration.GetSection("Identity").Bind(options));
}

builder.Services.AddSecureFactAuthentication();

var app = builder.Build();

// Operator commands. They run once and exit; they are never reachable over HTTP.
if (args.Contains("migrate", StringComparer.Ordinal))
{
    await TenancyModule.MigrateAsync(migrationsConnection ?? throw new InvalidOperationException("ConnectionStrings:Migrations is required."));
    await IdentityModule.MigrateAsync(migrationsConnection);
    await AuditModule.MigrateAsync(migrationsConnection);
    await OrganizationsModule.MigrateAsync(migrationsConnection);
    await BillingModule.MigrateAsync(migrationsConnection);
    return;
}

if (args.Contains("bootstrap-platform-admin", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var email = app.Configuration["SF_BOOTSTRAP_ADMIN_EMAIL"] ?? throw new InvalidOperationException("SF_BOOTSTRAP_ADMIN_EMAIL is required.");
    var password = app.Configuration["SF_BOOTSTRAP_ADMIN_PASSWORD"] ?? throw new InvalidOperationException("SF_BOOTSTRAP_ADMIN_PASSWORD is required.");
    var result = await scope.ServiceProvider.GetRequiredService<IPlatformBootstrapper>().EnsureFirstPlatformAdminAsync(email, password, CancellationToken.None);
    Console.WriteLine(result.IsSuccess
        ? (result.Value ? "Platform administrator created." : "A platform administrator already exists; nothing to do.")
        : $"Bootstrap failed: {result.Error.Code} {result.Error.Detail}");
    return;
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = c => c.Tags.Contains("live") }).AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapAuthEndpoints();
app.MapUserAndTenantEndpoints();
app.MapAuditEndpoints();
app.MapCompanyEndpoints();
app.MapBillingEndpoints();

app.Run();

/// <summary>Entry point marker so integration tests can host the API.</summary>
public partial class Program;
