using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecureFact.Customers.Application;
using SecureFact.Customers.Contracts;
using SecureFact.Customers.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Customers;

public static class CustomersModule
{
    /// <summary>Requires <c>AddPlatformDataScope</c>, the Audit module and the Catalogs module.</summary>
    public static IServiceCollection AddCustomersModule(this IServiceCollection services, string appConnectionString)
    {
        services.AddDbContext<CustomersDbContext>((sp, options) => options
            .UseNpgsql(appConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", CustomersDbContext.Schema))
            .AddInterceptors(new RlsConnectionInterceptor(sp.GetRequiredService<IDataScope>())));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ICustomerAdministration, CustomerAdministration>();
        return services;
    }

    /// <summary>Applies pending migrations. Must run with the schema-owner connection, never the runtime role.</summary>
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CustomersDbContext>()
            .UseNpgsql(ownerConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", CustomersDbContext.Schema))
            .Options;
        await using var db = new CustomersDbContext(options, new DataScope());
        await db.Database.MigrateAsync(cancellationToken);
    }
}
