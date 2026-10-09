using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Messaging;
using SecureFact.Webhooks.Application;
using SecureFact.Webhooks.Contracts;
using SecureFact.Webhooks.Infrastructure;

namespace SecureFact.Webhooks;

public static class WebhooksModule
{
    /// <summary>
    /// The webhooks (ADR-067). Requires <c>AddPlatformDataScope</c>, an <see cref="SecureFact.Platform.Security.ISecretProtector"/>, an <see cref="SecureFact.SharedKernel.Tenancy.ICurrentUser"/>, the Audit
    /// module and the CPE engine (to read the answer of SUNAT). The host that runs the outbox also delivers the events here, because the module consumes the events that are announced; the host that runs
    /// the workers sends them with <see cref="IWebhookDispatcher"/>. Reaching local addresses is refused in production.
    /// </summary>
    public static IServiceCollection AddWebhooksModule(this IServiceCollection services, string appConnectionString, WebhookOptions options, bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.AllowLocalTargets && isProduction)
        {
            throw new InvalidOperationException("Webhooks:AllowLocalTargets is not allowed in production: a webhook could reach the private network.");
        }

        services.AddSingleton(Options.Create(options));
        services.AddDbContext<WebhooksDbContext>((sp, builder) => builder
            .UseNpgsql(appConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", WebhooksDbContext.Schema))
            .AddInterceptors(new RlsConnectionInterceptor(sp.GetRequiredService<IDataScope>())));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IWebhooks, WebhookService>();
        services.AddScoped<DeliveryAttempt>();
        foreach (var eventType in WebhookFanOut.Handled)
        {
            services.AddScoped<IIntegrationEventConsumer>(sp => new WebhookFanOut(
                eventType, sp.GetRequiredService<WebhooksDbContext>(), sp.GetRequiredService<SecureFact.CpeEngine.Contracts.IElectronicDocumentService>(), sp.GetRequiredService<TimeProvider>()));
        }

        services.AddSingleton<IWebhookDispatcher, WebhookDispatcher>();

        // The connection is made by the module itself, to the addresses it has checked (WebhookTargets): neither the proxy nor a redirect can take a delivery somewhere else.
        services.AddHttpClient(DeliveryAttempt.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectCallback = (context, cancellationToken) => WebhookTargets.ConnectAsync(context, options.AllowLocalTargets, cancellationToken),
            });
        return services;
    }

    /// <summary>Applies pending migrations. Must run with the schema-owner connection, never the runtime role.</summary>
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<WebhooksDbContext>()
            .UseNpgsql(ownerConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", WebhooksDbContext.Schema))
            .Options;
        await using var db = new WebhooksDbContext(options, new DataScope());
        await db.Database.MigrateAsync(cancellationToken);
    }
}
