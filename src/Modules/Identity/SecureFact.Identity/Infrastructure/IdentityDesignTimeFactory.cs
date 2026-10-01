using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Identity.Infrastructure;

/// <summary>Used only by <c>dotnet ef</c>. Reads the owner connection from the environment; nothing is hard-coded.</summary>
internal sealed class IdentityDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("SF_MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Database=design_time_only";
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", IdentityDbContext.Schema))
            .Options;
        return new IdentityDbContext(options, new DataScope());
    }
}
