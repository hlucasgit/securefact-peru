using System.Text.RegularExpressions;

namespace SecureFact.Platform.Persistence;

public enum RlsMode
{
    /// <summary>Rows are visible only to the tenant that owns them.</summary>
    TenantOnly,

    /// <summary>Tenant isolation plus an explicit platform scope (e.g. the tenant registry, the outbox).</summary>
    TenantOrPlatform,
}

/// <summary>Generates the Row Level Security DDL used by every module migration (ADR-003).</summary>
public static partial class RlsSql
{
    public const string PolicyName = "tenant_isolation";
    public const string AppRole = "securefact_app";

    [GeneratedRegex("^[a-z_][a-z0-9_]*$")]
    private static partial Regex Identifier();

    /// <param name="schema">Module schema.</param>
    /// <param name="table">Table to protect.</param>
    /// <param name="mode">Whether an explicit platform scope may bypass tenant isolation.</param>
    /// <param name="tenantColumn">Column holding the owner tenant (<c>tenant_id</c>, or <c>id</c> for the tenant registry).</param>
    public static string Enable(string schema, string table, RlsMode mode = RlsMode.TenantOnly, string tenantColumn = "tenant_id")
    {
        Validate(schema);
        Validate(table);
        Validate(tenantColumn);

        var predicate = $"{tenantColumn} = NULLIF(current_setting('app.tenant_id', true), '')::uuid";
        if (mode == RlsMode.TenantOrPlatform)
        {
            predicate = $"{predicate} OR current_setting('app.scope', true) = 'platform'";
        }

        return $"""
            ALTER TABLE {schema}.{table} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {schema}.{table} FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS {PolicyName} ON {schema}.{table};
            CREATE POLICY {PolicyName} ON {schema}.{table} USING ({predicate}) WITH CHECK ({predicate});
            """;
    }

    /// <summary>
    /// Grants runtime privileges to the application role when it exists. The role is created by infrastructure
    /// (not by migrations) and has neither ownership of the tables nor BYPASSRLS.
    /// </summary>
    public static string GrantToAppRole(string schema, string privileges = "SELECT, INSERT, UPDATE")
    {
        Validate(schema);
        return $"""
            DO $grant$
            BEGIN
              IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{AppRole}') THEN
                GRANT USAGE ON SCHEMA {schema} TO {AppRole};
                GRANT {privileges} ON ALL TABLES IN SCHEMA {schema} TO {AppRole};
                ALTER DEFAULT PRIVILEGES IN SCHEMA {schema} GRANT {privileges} ON TABLES TO {AppRole};
              ELSE
                RAISE NOTICE 'Role {AppRole} not found; grants skipped for schema {schema}.';
              END IF;
            END
            $grant$;
            """;
    }

    private static void Validate(string identifier)
    {
        if (!Identifier().IsMatch(identifier))
        {
            throw new ArgumentException($"'{identifier}' is not a safe lowercase SQL identifier.", nameof(identifier));
        }
    }
}
