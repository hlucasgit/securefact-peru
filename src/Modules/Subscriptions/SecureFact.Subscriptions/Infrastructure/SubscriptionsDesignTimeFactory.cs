using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Subscriptions.Infrastructure;

/// <summary>Used only by <c>dotnet ef</c>. Reads the owner connection from the environment; nothing is hard-coded.</summary>
internal sealed class SubscriptionsDesignTimeFactory : IDesignTimeDbContextFactory<SubscriptionsDbContext>
{
    public SubscriptionsDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("SF_MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Database=design_time_only";
        var options = new DbContextOptionsBuilder<SubscriptionsDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", SubscriptionsDbContext.Schema))
            .Options;
        return new SubscriptionsDbContext(options, new DataScope());
    }
}
