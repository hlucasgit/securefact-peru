using Microsoft.EntityFrameworkCore;
using Npgsql;
using SecureFact.Platform.Persistence;
using SecureFact.SharedKernel.Domain;

namespace SecureFact.Security.Tests;

/// <summary>
/// Cross-tenant isolation tests (ADR-003). A failure here is a critical vulnerability and must block CI.
/// Every test acts as the runtime role, never as the schema owner.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class RowLevelSecurityTests(PostgresFixture postgres)
{
    private static readonly TenantId A = TenantId.New();
    private static readonly TenantId B = TenantId.New();

    private async Task SeedAsync(TenantId tenant, string body)
    {
        await using var db = postgres.NotesContext(PostgresFixture.TenantScope(tenant));
        db.Notes.Add(new Note { Body = body });
        await db.SaveChangesAsync();
    }

    private static async Task<List<string>> BodiesAsync(NotesDbContext db, bool ignoreFilters = false)
    {
        IQueryable<Note> query = db.Notes;
        if (ignoreFilters)
        {
            query = query.IgnoreQueryFilters();
        }

        return await query.Select(n => n.Body).OrderBy(b => b).ToListAsync();
    }

    [Fact]
    public async Task Tenant_reads_only_its_own_rows()
    {
        var tag = Guid.NewGuid().ToString("N");
        await SeedAsync(A, $"a-{tag}");
        await SeedAsync(B, $"b-{tag}");

        await using var db = postgres.NotesContext(PostgresFixture.TenantScope(A));
        var bodies = await BodiesAsync(db);

        Assert.Contains($"a-{tag}", bodies);
        Assert.DoesNotContain($"b-{tag}", bodies);
    }

    [Fact]
    public async Task Rls_still_isolates_when_the_application_filter_is_bypassed()
    {
        var tag = Guid.NewGuid().ToString("N");
        await SeedAsync(A, $"a-{tag}");
        await SeedAsync(B, $"b-{tag}");

        await using var db = postgres.NotesContext(PostgresFixture.TenantScope(A));
        var bodies = await BodiesAsync(db, ignoreFilters: true);

        Assert.Contains($"a-{tag}", bodies);
        Assert.DoesNotContain($"b-{tag}", bodies);
    }

    [Fact]
    public async Task Raw_sql_is_isolated_too()
    {
        var tag = Guid.NewGuid().ToString("N");
        await SeedAsync(A, $"a-{tag}");
        await SeedAsync(B, $"b-{tag}");

        await using var db = postgres.NotesContext(PostgresFixture.TenantScope(B));
        var bodies = await db.Database.SqlQueryRaw<string>("SELECT body AS \"Value\" FROM rlstest.notes").ToListAsync();

        Assert.Contains($"b-{tag}", bodies);
        Assert.DoesNotContain($"a-{tag}", bodies);
    }

    [Fact]
    public async Task Anonymous_scope_sees_no_rows()
    {
        await SeedAsync(A, "visible-to-a-only");

        await using var db = postgres.NotesContext(new SecureFact.Platform.Tenancy.DataScope());
        var count = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM rlstest.notes").SingleAsync();

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Anonymous_scope_cannot_insert()
    {
        await using var db = postgres.NotesContext(new SecureFact.Platform.Tenancy.DataScope());

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO rlstest.notes (id, tenant_id, body) VALUES ({Guid.NewGuid()}, {A.Value}, 'x')"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Database_rejects_inserting_a_row_for_another_tenant_even_when_the_application_guard_is_skipped()
    {
        await using var db = postgres.NotesContext(PostgresFixture.TenantScope(A));

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO rlstest.notes (id, tenant_id, body) VALUES ({Guid.NewGuid()}, {B.Value}, 'forged')"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Application_guard_rejects_adding_a_row_owned_by_another_tenant()
    {
        await using var db = postgres.NotesContext(PostgresFixture.TenantScope(A));
        db.Notes.Add(new Note { TenantId = B.Value, Body = "forged" });

        await Assert.ThrowsAsync<TenantMismatchException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Application_guard_rejects_creating_a_row_without_a_tenant_scope()
    {
        await using var db = postgres.NotesContext(new SecureFact.Platform.Tenancy.DataScope());
        db.Notes.Add(new Note { Body = "orphan" });

        await Assert.ThrowsAsync<TenantMismatchException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Application_guard_rejects_changing_the_owner_of_a_row()
    {
        await SeedAsync(A, "mine");
        await using var db = postgres.NotesContext(PostgresFixture.TenantScope(A));
        var note = await db.Notes.FirstAsync(n => n.Body == "mine");
        note.TenantId = B.Value;

        await Assert.ThrowsAsync<TenantMismatchException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Update_and_delete_of_another_tenants_row_affect_nothing()
    {
        var tag = Guid.NewGuid().ToString("N");
        await SeedAsync(B, $"b-{tag}");

        await using var db = postgres.NotesContext(PostgresFixture.TenantScope(A));
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE rlstest.notes SET body = 'hacked' WHERE body = {$"b-{tag}"}");
        var deleted = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM rlstest.notes WHERE body = {$"b-{tag}"}");

        Assert.Equal(0, updated);
        Assert.Equal(0, deleted);

        await using var owner = postgres.NotesContext(PostgresFixture.TenantScope(B));
        Assert.Contains($"b-{tag}", await BodiesAsync(owner));
    }

    [Fact]
    public async Task Tenant_scope_set_on_a_pooled_connection_does_not_leak_to_the_next_user()
    {
        var tag = Guid.NewGuid().ToString("N");
        await SeedAsync(A, $"a-{tag}");

        // The fixture pool has a single connection, so the next context reuses the one that just served tenant A.
        await using (var first = postgres.NotesContext(PostgresFixture.TenantScope(A)))
        {
            Assert.Contains($"a-{tag}", await BodiesAsync(first));
        }

        await using var second = postgres.NotesContext(new SecureFact.Platform.Tenancy.DataScope());
        var count = await second.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM rlstest.notes").SingleAsync();

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Platform_scope_is_cross_tenant_only_where_the_table_allows_it()
    {
        var tag = Guid.NewGuid().ToString("N");
        await using (var seedA = postgres.NotesContext(PostgresFixture.TenantScope(A)))
        {
            await seedA.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO rlstest.platform_notes (id, tenant_id, body) VALUES ({Guid.NewGuid()}, {A.Value}, {$"a-{tag}"})");
        }

        await using (var seedB = postgres.NotesContext(PostgresFixture.TenantScope(B)))
        {
            await seedB.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO rlstest.platform_notes (id, tenant_id, body) VALUES ({Guid.NewGuid()}, {B.Value}, {$"b-{tag}"})");
        }

        await using var platform = postgres.NotesContext(PostgresFixture.PlatformScope());
        var open = await platform.Database.SqlQueryRaw<string>("SELECT body AS \"Value\" FROM rlstest.platform_notes").ToListAsync();
        var strict = await platform.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM rlstest.notes").SingleAsync();

        Assert.Contains($"a-{tag}", open);
        Assert.Contains($"b-{tag}", open);
        Assert.Equal(0, strict);
    }

    [Fact]
    public async Task Runtime_role_is_not_privileged_and_owns_nothing()
    {
        await using var db = postgres.NotesContext(PostgresFixture.TenantScope(A));

        var bypass = await db.Database.SqlQueryRaw<bool>(
            "SELECT (rolbypassrls OR rolsuper) AS \"Value\" FROM pg_roles WHERE rolname = current_user").SingleAsync();
        var ownedTables = await db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM pg_tables WHERE tableowner = current_user").SingleAsync();

        Assert.False(bypass);
        Assert.Equal(0, ownedTables);
    }

    [Fact]
    public async Task Every_table_in_every_schema_has_forced_rls_and_a_policy()
    {
        var unprotected = await postgres.ScalarAsOwnerAsync<string>("""
            SELECT coalesce(string_agg(n.nspname || '.' || c.relname, ', '), '')
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p')
              AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
              AND c.relname <> '__ef_migrations_history'
              AND (NOT c.relrowsecurity
                   OR NOT c.relforcerowsecurity
                   OR NOT EXISTS (SELECT 1 FROM pg_policies p WHERE p.schemaname = n.nspname AND p.tablename = c.relname))
            """);

        Assert.True(unprotected.Length == 0, $"Tables without forced RLS and a policy: {unprotected}");
    }
}
