using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Audit.Infrastructure;

/// <summary>Used only by <c>dotnet ef</c>. Reads the owner connection from the environment; nothing is hard-coded.</summary>
internal sealed class AuditDesignTimeFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("SF_MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Database=design_time_only";
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AuditDbContext.Schema))
            .Options;
        return new AuditDbContext(options, new DataScope());
    }
}
