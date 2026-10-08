using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SecureFact.Notifications.Infrastructure;

/// <summary>Used only by <c>dotnet ef</c>. Reads the owner connection from the environment; nothing is hard-coded.</summary>
internal sealed class NotificationsDesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("SF_MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Database=design_time_only";
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", NotificationsDbContext.Schema))
            .Options;
        return new NotificationsDbContext(options);
    }
}
