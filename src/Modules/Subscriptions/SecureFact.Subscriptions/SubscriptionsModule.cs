using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;
using SecureFact.Subscriptions.Application;
using SecureFact.Subscriptions.Contracts;
using SecureFact.Subscriptions.Infrastructure;

namespace SecureFact.Subscriptions;

public static class SubscriptionsModule
{
    /// <summary>
    /// Prices, commissions, charges and payments of the platform (ADR-062, ADR-063, ADR-064). Requires <c>AddPlatformDataScope</c> and the Audit, Tenancy, Billing and Rules modules. The charges of the
    /// months that closed and the suspensions for non-payment are made by <see cref="ICollectionProcessor"/>, which the workers run.
    /// </summary>
    public static IServiceCollection AddSubscriptionsModule(this IServiceCollection services, string appConnectionString)
    {
        services.AddDbContext<SubscriptionsDbContext>((sp, options) => options
            .UseNpgsql(appConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", SubscriptionsDbContext.Schema))
            .AddInterceptors(new RlsConnectionInterceptor(sp.GetRequiredService<IDataScope>())));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IPricing, Pricing>();
        services.AddScoped<ICollections, Collections>();
        services.AddScoped<Commissions>();
        services.AddScoped<ICommissions>(sp => sp.GetRequiredService<Commissions>());
        services.AddScoped<Enforcement>();
        services.AddScoped<IssuerAccess>();
        services.AddScoped<ChargeInvoicing>();
        services.AddScoped<IBillingProfiles, BillingProfiles>();
        services.AddScoped<IInvoicingSettings, InvoicingSettingsService>();
        services.AddScoped<IChargeDocuments, ChargeDocumentFiles>();
        services.TryAddScoped<SecureFact.Notifications.Contracts.IBillingNotices, SecureFact.Notifications.Contracts.NullBillingNotices>();
        services.AddScoped<BillingReminders>();
        services.AddScoped<CollectionPass>();
        services.AddSingleton<ICollectionProcessor, CollectionProcessor>();
        return services;
    }

    /// <summary>Applies pending migrations. Must run with the schema-owner connection, never the runtime role.</summary>
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<SubscriptionsDbContext>()
            .UseNpgsql(ownerConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", SubscriptionsDbContext.Schema))
            .Options;
        await using var db = new SubscriptionsDbContext(options, new DataScope());
        await db.Database.MigrateAsync(cancellationToken);
    }
}
