using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecureFact.Organizations.Application;
using SecureFact.Organizations.Contracts;
using SecureFact.Organizations.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Organizations;

public static class OrganizationsModule
{
    /// <summary>Requires <c>AddPlatformDataScope</c> and the Audit module.</summary>
    public static IServiceCollection AddOrganizationsModule(this IServiceCollection services, string appConnectionString)
    {
        services.AddDbContext<OrganizationsDbContext>((sp, options) => options
            .UseNpgsql(appConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", OrganizationsDbContext.Schema))
            .AddInterceptors(new RlsConnectionInterceptor(sp.GetRequiredService<IDataScope>())));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ICompanyAdministration, CompanyAdministration>();
        return services;
    }

    /// <summary>Applies pending migrations. Must run with the schema-owner connection, never the runtime role.</summary>
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseNpgsql(ownerConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", OrganizationsDbContext.Schema))
            .Options;
        await using var db = new OrganizationsDbContext(options, new DataScope());
        await db.Database.MigrateAsync(cancellationToken);
    }
}
