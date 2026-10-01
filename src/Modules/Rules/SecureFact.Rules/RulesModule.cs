using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;
using SecureFact.Rules.Application;
using SecureFact.Rules.Contracts;
using SecureFact.Rules.Infrastructure;

namespace SecureFact.Rules;

public static class RulesModule
{
    /// <summary>Rules are platform-wide reference data: every tenant is evaluated with the same regulation.</summary>
    public static IServiceCollection AddRulesModule(this IServiceCollection services, string appConnectionString)
    {
        services.AddDbContext<RulesDbContext>((sp, options) => options
            .UseNpgsql(appConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", RulesDbContext.Schema))
            .AddInterceptors(new RlsConnectionInterceptor(sp.GetRequiredService<IDataScope>())));
        services.AddScoped<IRuleProvider, RuleProvider>();
        return services;
    }

    /// <summary>Applies pending migrations and loads the rule seed. Must run with the schema-owner connection.</summary>
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        var scope = new DataScope();
        scope.UsePlatform("rules:migrate");
        var options = new DbContextOptionsBuilder<RulesDbContext>()
            .UseNpgsql(ownerConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", RulesDbContext.Schema))
            .AddInterceptors(new RlsConnectionInterceptor(scope))
            .Options;
        await using var db = new RulesDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);
        await RuleSeeder.SeedAsync(db, cancellationToken);
    }
}
