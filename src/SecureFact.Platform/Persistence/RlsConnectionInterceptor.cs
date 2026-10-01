using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Platform.Persistence;

/// <summary>
/// Pushes the current <see cref="IDataScope"/> into PostgreSQL session variables (<c>app.tenant_id</c>, <c>app.scope</c>)
/// every time a connection is opened and clears them just before it returns to the pool, so RLS policies
/// always see the scope of the operation that owns the connection (ADR-003).
/// </summary>
public sealed class RlsConnectionInterceptor(IDataScope scope) : DbConnectionInterceptor
{
    public const string PlatformScopeValue = "platform";

    private const string SetSql = "SELECT set_config('app.tenant_id', @tenant, false), set_config('app.scope', @scope, false)";
    private const string ResetSql = "RESET app.tenant_id; RESET app.scope";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateSetCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = CreateSetCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public override InterceptionResult ConnectionClosing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        if (connection.State == ConnectionState.Open)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ResetSql;
            command.ExecuteNonQuery();
        }

        return result;
    }

    public override async ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        if (connection.State == ConnectionState.Open)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ResetSql;
            await command.ExecuteNonQueryAsync();
        }

        return result;
    }

    private NpgsqlCommand CreateSetCommand(DbConnection connection)
    {
        var command = ((NpgsqlConnection)connection).CreateCommand();
        command.CommandText = SetSql;
        command.Parameters.AddWithValue("tenant", scope.Current?.Value.ToString("D") ?? string.Empty);
        command.Parameters.AddWithValue("scope", scope.Kind == DataScopeKind.Platform ? PlatformScopeValue : string.Empty);
        return command;
    }
}
