using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecureFact.Audit.Application;
using SecureFact.Audit.Contracts;
using SecureFact.Audit.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Audit;

public static class AuditModule
{
    /// <summary>Requires <c>AddPlatformDataScope</c>, an <c>ICurrentUser</c> and an <c>IRequestContext</c> from the host.</summary>
    public static IServiceCollection AddAuditModule(this IServiceCollection services, string appConnectionString)
    {
        services.AddDbContext<AuditDbContext>((sp, options) => options
            .UseNpgsql(appConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AuditDbContext.Schema))
            .AddInterceptors(new RlsConnectionInterceptor(sp.GetRequiredService<IDataScope>())));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IAuditTrail, AuditTrail>();
        services.AddScoped<IAuditQuery, AuditQuery>();
        return services;
    }

    /// <summary>Applies pending migrations. Must run with the schema-owner connection, never the runtime role.</summary>
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(ownerConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AuditDbContext.Schema))
            .Options;
        await using var db = new AuditDbContext(options, new DataScope());
        await db.Database.MigrateAsync(cancellationToken);
    }
}
