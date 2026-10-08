using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecureFact.Gre.Application;
using SecureFact.Gre.Contracts;
using SecureFact.Gre.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Gre;

public static class GreModule
{
    /// <summary>
    /// The guides of the sender (ADR-056). Requires <c>AddPlatformDataScope</c>, the Audit, Catalogs, Certificates, Organizations and Rules modules and <c>AddCpeEngineModule</c> (signing and packaging).
    /// Without a call to <see cref="AddGreSubmissionChannel"/> or <see cref="AddSandboxGreChannel"/> the guides are prepared and wait: SUNAT is never reached implicitly.
    /// </summary>
    public static IServiceCollection AddGreModule(this IServiceCollection services, string appConnectionString)
    {
        services.AddDbContext<GreDbContext>((sp, options) => options
            .UseNpgsql(appConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", GreDbContext.Schema))
            .AddInterceptors(new RlsConnectionInterceptor(sp.GetRequiredService<IDataScope>())));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IGreChannel, UnconfiguredGreChannel>();
        services.AddScoped<IGreService, GreService>();
        services.AddScoped<IGreSeriesAdministration, GreSeriesAdministration>();
        services.AddSingleton<IGreWorkProcessor, GreWorkProcessor>();
        return services;
    }

    /// <summary>Registers the REST channel to SUNAT. The addresses are explicit: production is never chosen implicitly. Call it after <see cref="AddGreModule"/>.</summary>
    public static IServiceCollection AddGreSubmissionChannel(this IServiceCollection services, GreChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddHttpClient<IGreChannel, GreRestChannel>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        return services;
    }

    /// <summary>The in-process simulator (ADR-039, ADR-056), refused in production. Call it after <see cref="AddGreModule"/>.</summary>
    public static IServiceCollection AddSandboxGreChannel(this IServiceCollection services, bool isProduction)
    {
        if (isProduction)
        {
            throw new InvalidOperationException("Sunat:Environment 'Sandbox' (the SUNAT simulator) is not allowed in production.");
        }

        services.AddSingleton<IGreChannel, SandboxGreChannel>();
        return services;
    }

    /// <summary>Applies pending migrations. Must run with the schema-owner connection, never the runtime role.</summary>
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<GreDbContext>()
            .UseNpgsql(ownerConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", GreDbContext.Schema))
            .Options;
        await using var db = new GreDbContext(options, new DataScope());
        await db.Database.MigrateAsync(cancellationToken);
    }
}
