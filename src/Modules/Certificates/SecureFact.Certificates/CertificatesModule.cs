using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecureFact.Certificates.Application;
using SecureFact.Certificates.Contracts;
using SecureFact.Certificates.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Certificates;

public static class CertificatesModule
{
    /// <summary>Requires <c>AddPlatformDataScope</c>, an <c>ISecretProtector</c>, the Audit module and the Organizations module.</summary>
    public static IServiceCollection AddCertificatesModule(this IServiceCollection services, string appConnectionString)
    {
        services.AddDbContext<CertificatesDbContext>((sp, options) => options
            .UseNpgsql(appConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", CertificatesDbContext.Schema))
            .AddInterceptors(new RlsConnectionInterceptor(sp.GetRequiredService<IDataScope>())));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<CertificateService>();
        services.AddScoped<ICertificateAdministration>(sp => sp.GetRequiredService<CertificateService>());
        services.AddScoped<ICertificateProvider>(sp => sp.GetRequiredService<CertificateService>());
        return services;
    }

    /// <summary>Applies pending migrations. Must run with the schema-owner connection, never the runtime role.</summary>
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CertificatesDbContext>()
            .UseNpgsql(ownerConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", CertificatesDbContext.Schema))
            .Options;
        await using var db = new CertificatesDbContext(options, new DataScope());
        await db.Database.MigrateAsync(cancellationToken);
    }
}
