using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Billing.Infrastructure;

/// <summary>Used only by <c>dotnet ef</c>. Reads the owner connection from the environment; nothing is hard-coded.</summary>
internal sealed class BillingDesignTimeFactory : IDesignTimeDbContextFactory<BillingDbContext>
{
    public BillingDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("SF_MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Database=design_time_only";
        var options = new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", BillingDbContext.Schema))
            .Options;
        return new BillingDbContext(options, new DataScope());
    }
}
