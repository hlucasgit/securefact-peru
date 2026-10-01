using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecureFact.Products.Application;
using SecureFact.Products.Contracts;
using SecureFact.Products.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Products;

public static class ProductsModule
{
    /// <summary>Requires <c>AddPlatformDataScope</c>, the Audit module and the Catalogs module.</summary>
    public static IServiceCollection AddProductsModule(this IServiceCollection services, string appConnectionString)
    {
        services.AddDbContext<ProductsDbContext>((sp, options) => options
            .UseNpgsql(appConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ProductsDbContext.Schema))
            .AddInterceptors(new RlsConnectionInterceptor(sp.GetRequiredService<IDataScope>())));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IProductAdministration, ProductAdministration>();
        return services;
    }

    /// <summary>Applies pending migrations. Must run with the schema-owner connection, never the runtime role.</summary>
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<ProductsDbContext>()
            .UseNpgsql(ownerConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ProductsDbContext.Schema))
            .Options;
        await using var db = new ProductsDbContext(options, new DataScope());
        await db.Database.MigrateAsync(cancellationToken);
    }
}
