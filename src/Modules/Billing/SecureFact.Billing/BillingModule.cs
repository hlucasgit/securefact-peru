using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecureFact.Billing.Application;
using SecureFact.Billing.Contracts;
using SecureFact.Billing.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Billing;

public static class BillingModule
{
    /// <summary>Requires <c>AddPlatformDataScope</c>, the Audit, Organizations and TaxEngine modules.</summary>
    public static IServiceCollection AddBillingModule(this IServiceCollection services, string appConnectionString)
    {
        services.AddDbContext<BillingDbContext>((sp, options) => options
            .UseNpgsql(appConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", BillingDbContext.Schema))
            .AddInterceptors(new RlsConnectionInterceptor(sp.GetRequiredService<IDataScope>())));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ISeriesAdministration, SeriesAdministration>();
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<SecureFact.SharedKernel.Messaging.IOutboxSource, BillingOutboxSource>();
        return services;
    }

    /// <summary>Applies pending migrations. Must run with the schema-owner connection, never the runtime role.</summary>
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql(ownerConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", BillingDbContext.Schema))
            .Options;
        await using var db = new BillingDbContext(options, new DataScope());
        await db.Database.MigrateAsync(cancellationToken);
    }
}
