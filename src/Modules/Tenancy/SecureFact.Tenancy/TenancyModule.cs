using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;
using SecureFact.Tenancy.Application;
using SecureFact.Tenancy.Contracts;
using SecureFact.Tenancy.Infrastructure;

namespace SecureFact.Tenancy;

public static class TenancyModule
{
    /// <summary>Registers the module. <paramref name="appConnectionString"/> must use the runtime role (no BYPASSRLS, not the table owner).</summary>
    public static IServiceCollection AddTenancyModule(this IServiceCollection services, string appConnectionString)
    {
        services.AddDbContext<TenancyDbContext>((sp, options) => options
            .UseNpgsql(appConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", TenancyDbContext.Schema))
            .AddInterceptors(new RlsConnectionInterceptor(sp.GetRequiredService<IDataScope>())));
        services.AddScoped<ITenantAdministration, TenantAdministration>();
        return services;
    }

    /// <summary>Applies pending migrations. Must run with the schema-owner connection, never the runtime role.</summary>
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(ownerConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", TenancyDbContext.Schema))
            .Options;
        await using var db = new TenancyDbContext(options, new DataScope());
        await db.Database.MigrateAsync(cancellationToken);
    }
}
