using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecureFact.Catalogs.Application;
using SecureFact.Catalogs.Contracts;
using SecureFact.Catalogs.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Catalogs;

public static class CatalogsModule
{
    /// <summary>Catalogues are platform-wide reference data: every tenant reads the same rows.</summary>
    public static IServiceCollection AddCatalogsModule(this IServiceCollection services, string appConnectionString)
    {
        services.AddDbContext<CatalogsDbContext>((sp, options) => options
            .UseNpgsql(appConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", CatalogsDbContext.Schema))
            .AddInterceptors(new RlsConnectionInterceptor(sp.GetRequiredService<IDataScope>())));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ICatalogReader, CatalogReader>();
        return services;
    }

    /// <summary>Applies pending migrations and loads the catalogue seed. Must run with the schema-owner connection.</summary>
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        var scope = new DataScope();
        scope.UsePlatform("catalogs:migrate");
        var options = new DbContextOptionsBuilder<CatalogsDbContext>()
            .UseNpgsql(ownerConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", CatalogsDbContext.Schema))
            .AddInterceptors(new RlsConnectionInterceptor(scope))
            .Options;
        await using var db = new CatalogsDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);
        await CatalogSeeder.SeedAllAsync(db, TimeProvider.System, cancellationToken);
    }
}
