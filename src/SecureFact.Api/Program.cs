using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SecureFact.Api.Endpoints;
using SecureFact.Api.Infrastructure;
using SecureFact.Api.Security;
using SecureFact.Audit;
using SecureFact.Billing;
using SecureFact.Catalogs;
using SecureFact.CpeEngine;
using SecureFact.Certificates;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Gre;
using SecureFact.Gre.Contracts;
using SecureFact.Subscriptions;
using SecureFact.Webhooks;
using SecureFact.Webhooks.Application;
using SecureFact.Customers;
using SecureFact.Products;
using SecureFact.Identity;
using SecureFact.Organizations;
using SecureFact.Identity.Contracts;
using SecureFact.Notifications;
using SecureFact.Platform;
using SecureFact.Platform.Security;
using SecureFact.Platform.Storage;
using SecureFact.Storage.S3;
using SecureFact.Rules;
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
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info = new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "SecureFact Perú API",
        Version = "v1",
        Description = "API pública de SecureFact Perú. Autenticación: llave de API (Authorization: Bearer sfk_… o X-Api-Key) o token de acceso de una persona. Errores: Problem Details (RFC 9457) con códigos SF-<ÁREA>-nnn. Idempotencia: encabezado Idempotency-Key al emitir. La guía está en docs/api/README.md.",
    };
    document.Components ??= new Microsoft.OpenApi.OpenApiComponents();
    document.Components.SecuritySchemes ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>();
    document.Components.SecuritySchemes["bearer"] = new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "Access token or sfk_ API key",
        Description = "Authorization: Bearer <token de acceso o llave sfk_…>",
    };
    return Task.CompletedTask;
}));
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
// Documents are archived in object storage (ADR-005, ADR-036): S3 when a bucket is configured. Without one only a development machine or a test may use the in-memory storage.
if (!string.IsNullOrWhiteSpace(builder.Configuration["Storage:S3:Bucket"]))
{
    builder.Services.AddS3ObjectStorage(builder.Configuration.GetSection(S3StorageOptions.SectionName));
}
else if (builder.Environment.IsProduction())
{
    throw new InvalidOperationException("Storage:S3:Bucket is required in production: the documents cannot be archived in memory (ADR-005).");
}
else
{
    builder.Services.AddInMemoryObjectStorage();
}
builder.Services.AddNotificationsModule(builder.Configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new EmailOptions(), builder.Environment.IsProduction());
builder.Services.AddPortalLinks(builder.Configuration.GetSection(WebOptions.SectionName).Get<WebOptions>() ?? new WebOptions(), builder.Environment.IsProduction());
builder.Services.AddScoped<IPasswordResetNotifier, EmailPasswordResetNotifier>();
builder.Services.AddSecureFactRateLimiting(builder.Configuration);
builder.Services.AddScoped<IRequestContext, HttpRequestContext>();

if (appConnection is not null)
{
    builder.Services.AddAuditModule(appConnection);
    builder.Services.AddTenancyModule(appConnection);
    builder.Services.AddDomainProvisioning(builder.Configuration.GetSection(DomainsOptions.SectionName).Get<DomainsOptions>() ?? new DomainsOptions(), builder.Environment.IsProduction());
    builder.Services.AddPlanUsage();
    builder.Services.AddEmailNotices(appConnection);
    builder.Services.AddOrganizationsModule(appConnection);
    builder.Services.AddTaxEngineModule();
    builder.Services.AddCpeEngineModule();
    builder.Services.AddCatalogsModule(appConnection);
    builder.Services.AddRulesModule(appConnection);
    builder.Services.AddCustomersModule(appConnection);
    builder.Services.AddCertificatesModule(appConnection);
    builder.Services.AddCpePipeline(appConnection);
    builder.Services.AddGreModule(appConnection);
    builder.Services.AddSubscriptionsModule(appConnection);
    builder.Services.AddWebhooksModule(appConnection, builder.Configuration.GetSection(WebhookOptions.SectionName).Get<WebhookOptions>() ?? new WebhookOptions(), builder.Environment.IsProduction());

    // SUNAT is never reached implicitly: the environment must be named. Without it, documents are prepared and signed but not sent.
    switch (builder.Configuration["Sunat:Environment"])
    {
        case "Beta":
            // SUNAT documents no beta for the guides (R-065): they stay prepared in this environment.
            builder.Services.AddSunatSubmissionChannel(SunatChannelOptions.Beta);
            break;
        case "Production":
            builder.Services.AddSunatSubmissionChannel(SunatChannelOptions.Production);
            builder.Services.AddGreSubmissionChannel(GreChannelOptions.Production);
            break;
        case "Sandbox":
            // The in-process simulator (ADR-039): for development and end-to-end tests, never production.
            builder.Services.AddSandboxSubmissionChannel(builder.Environment.IsProduction());
            builder.Services.AddSandboxGreChannel(builder.Environment.IsProduction());
            break;
        case null or "":
            break;
        default:
            throw new InvalidOperationException("Sunat:Environment must be 'Beta', 'Production' or 'Sandbox'.");
    }
    builder.Services.AddProductsModule(appConnection);
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
    await NotificationsModule.MigrateAsync(migrationsConnection);
    await AuditModule.MigrateAsync(migrationsConnection);
    await OrganizationsModule.MigrateAsync(migrationsConnection);
    await BillingModule.MigrateAsync(migrationsConnection);
    await CatalogsModule.MigrateAsync(migrationsConnection);
    await RulesModule.MigrateAsync(migrationsConnection);
    await CustomersModule.MigrateAsync(migrationsConnection);
    await CertificatesModule.MigrateAsync(migrationsConnection);
    await SecureFact.CpeEngine.CpeEngineModule.MigrateAsync(migrationsConnection);
    await SecureFact.Gre.GreModule.MigrateAsync(migrationsConnection);
    await SecureFact.Subscriptions.SubscriptionsModule.MigrateAsync(migrationsConnection);
    await WebhooksModule.MigrateAsync(migrationsConnection);
    await ProductsModule.MigrateAsync(migrationsConnection);
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
app.UseMiddleware<SupportAccessGuard>();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = c => c.Tags.Contains("live") }).AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

// The description of the API is public in every environment: it is the contract that integrators build on (ADR-066).
app.MapOpenApi().AllowAnonymous();

app.MapAuthEndpoints();
app.MapUserAndTenantEndpoints();
app.MapPlanEndpoints();
app.MapApiKeyEndpoints();
app.MapSupportAccessEndpoints();
app.MapWebhookEndpoints();
app.MapResellerEndpoints();
app.MapBrandingEndpoints();
app.MapDomainEndpoints();
app.MapAuditEndpoints();
app.MapCompanyEndpoints();
app.MapBillingEndpoints();
app.MapCatalogEndpoints();
app.MapRuleEndpoints();
app.MapMasterDataEndpoints();
app.MapCertificateEndpoints();
app.MapCpeEndpoints();
app.MapGreEndpoints();
app.MapSubscriptionEndpoints();
app.MapOutboxEndpoints();

app.Run();

/// <summary>Entry point marker so integration tests can host the API.</summary>
public partial class Program;
