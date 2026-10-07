using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        services.AddMemoryCache();
        services.AddScoped<ITenantAdministration, TenantAdministration>();
        services.AddScoped<ITenantStatusReader, TenantStatusReader>();
        services.AddScoped<IPlanAdministration, PlanAdministration>();
        services.AddScoped<IPlanLimits, PlanLimitsReader>();
        services.AddScoped<IResellerAdministration, ResellerAdministration>();
        services.AddScoped<IBranding, BrandingService>();
        return services;
    }

    /// <summary>
    /// Registers the domains of the resellers (ADR-051): the verification of their DNS records and the answer to the edge. The DNS lookups are real unless <c>Domains:Dns:Provider</c> is
    /// <c>Sandbox</c> (every check passes), which is for development and tests and is refused in production.
    /// </summary>
    public static IServiceCollection AddDomainProvisioning(this IServiceCollection services, DomainsOptions options, bool isProduction)
    {
        if (options.Dns.Provider is not ("System" or "Sandbox"))
        {
            throw new InvalidOperationException("Domains:Dns:Provider must be 'System' or 'Sandbox'.");
        }

        if (isProduction && string.Equals(options.Dns.Provider, "Sandbox", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Domains:Dns:Provider 'Sandbox' (every check passes) is not allowed in production.");
        }

        services.AddSingleton(options);
        services.TryAddSingleton<IDomainNameSystem, SystemDomainNameSystem>();
        services.AddScoped<IDomains, DomainService>();
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
