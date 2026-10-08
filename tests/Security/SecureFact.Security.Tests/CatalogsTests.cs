using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SecureFact.Catalogs;
using SecureFact.Catalogs.Application;
using SecureFact.Catalogs.Contracts;
using SecureFact.Catalogs.Infrastructure;
using SecureFact.Identity.Contracts;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;

namespace SecureFact.Security.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class CatalogsTests(ApiFixture api)
{
    private async Task<HttpClient> TenantClientAsync(string name)
    {
        var tenantId = await api.CreateTenantAsync(name);
        using var admin = await api.AdminClientAsync();
        var user = await ApiFixture.CreateUserAsync(admin, Roles.ReadOnly, tenantId);
        return api.ClientFor(await api.LoginOkAsync(user.Email, user.Password));
    }

    [Fact]
    public async Task Catalogues_require_authentication()
    {
        using var anonymous = api.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/catalogs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/catalogs/07")).StatusCode);
    }

    [Fact]
    public async Task Any_tenant_user_reads_the_official_catalogues()
    {
        using var client = await TenantClientAsync("Catalog Reader SAC");

        var summaries = (await client.GetFromJsonAsync<List<CatalogSummaryDto>>("/api/v1/catalogs", ApiFixture.JsonOptions))!;
        Assert.True(summaries.Count >= 40);
        Assert.Contains(summaries, s => s.Number == "07" && s.EntryCount == 19);

        var igv = (await client.GetFromJsonAsync<List<CatalogEntryDto>>("/api/v1/catalogs/07", ApiFixture.JsonOptions))!;
        var gravado = Assert.Single(igv, e => e.Code == "10");
        Assert.Equal("1000", gravado.Metadata["Codigo de tributo"]);
        Assert.Equal(1, gravado.Version);
        Assert.Equal("reglas-de-validacion-2026-08-26.xlsx", gravado.Source);
    }

    [Fact]
    public async Task The_catalogues_of_the_gre_come_from_the_newer_workbook_of_the_gre_and_the_rest_from_the_one_of_the_cpe()
    {
        using var client = await TenantClientAsync("Catalog Gre SAC");

        var motives = (await client.GetFromJsonAsync<List<CatalogEntryDto>>("/api/v1/catalogs/20", ApiFixture.JsonOptions))!;
        Assert.Equal(14, motives.Count);
        Assert.Contains(motives, e => e.Code == "19" && e.Version == 2 && e.Source == "reglas-validacion-publicado-2026-09-25.xlsx");
        var related = (await client.GetFromJsonAsync<List<CatalogEntryDto>>("/api/v1/catalogs/61", ApiFixture.JsonOptions))!;
        Assert.Equal("solo transportista", Assert.Single(related, e => e.Code == "31").Metadata["GRE Aplicable"]);
        var igv = (await client.GetFromJsonAsync<List<CatalogEntryDto>>("/api/v1/catalogs/07", ApiFixture.JsonOptions))!;
        Assert.All(igv, e => Assert.Equal(1, e.Version)); // a catalogue the GRE does not use stays as the CPE gave it

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/catalogs/18")).StatusCode);
    }

    [Fact]
    public async Task Unknown_catalogues_are_reported_with_a_stable_code()
    {
        using var client = await TenantClientAsync("Catalog Unknown SAC");

        var response = await client.GetAsync("/api/v1/catalogs/999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("SF-CAT-001", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_runtime_role_cannot_change_reference_data_even_in_platform_scope()
    {
        await using var connection = new NpgsqlConnection(api.Postgres.AppConnectionString);
        await connection.OpenAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('app.scope', 'platform', false)", connection))
        {
            await scope.ExecuteNonQueryAsync();
        }

        foreach (var sql in new[]
        {
            "UPDATE catalog.catalog_entry SET description = 'tampered'",
            "DELETE FROM catalog.catalog_entry",
            "INSERT INTO catalog.catalog_entry (id, catalog_number, code, description, version, effective_from, source, active, metadata) VALUES (gen_random_uuid(), '99', 'x', 'x', 1, '1900-01-01', 'x', true, '{}')",
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    [Fact]
    public async Task Loading_the_same_seed_twice_changes_nothing()
    {
        var before = await api.Postgres.ScalarAsOwnerAsync<long>("SELECT count(*) FROM catalog.catalog_entry");

        await CatalogsModule.MigrateAsync(api.Postgres.OwnerConnectionString);

        Assert.Equal(before, await api.Postgres.ScalarAsOwnerAsync<long>("SELECT count(*) FROM catalog.catalog_entry"));
        Assert.Equal(1, await api.Postgres.ScalarAsOwnerAsync<int>("SELECT max(version) FROM catalog.catalog_edition WHERE catalog_number = '07'"));
    }

    [Fact]
    public async Task A_newer_source_becomes_a_new_version_and_old_dates_keep_the_old_codes()
    {
        // Own database state is shared by the fixture, so this test only touches catalogue 08 and restores nothing it did not create.
        var scope = new DataScope();
        scope.UsePlatform("catalog versioning test");
        var options = new DbContextOptionsBuilder<CatalogsDbContext>()
            .UseNpgsql(api.Postgres.OwnerConnectionString)
            .AddInterceptors(new RlsConnectionInterceptor(scope))
            .Options;
        await using var db = new CatalogsDbContext(options);

        var current = CatalogSeeder.LoadEmbedded();
        var amended = new CatalogSeeder.SeedFile(
            new CatalogSeeder.SeedSource("reglas-de-validacion-2027-01-01.xlsx", 1, new string('a', 64), current.Source.Sheet),
            [new CatalogSeeder.SeedCatalog("08", "Código de tipos de sistema de cálculo del ISC", ["Código", "Descripción"],
                [new CatalogSeeder.SeedEntry("01", "Sistema al valor (modificado)", new Dictionary<string, string>()), new CatalogSeeder.SeedEntry("04", "Sistema nuevo", new Dictionary<string, string>())])]);

        var loaded = await CatalogSeeder.SeedAsync(db, TimeProvider.System, CancellationToken.None, amended);
        Assert.Equal(1, loaded);

        var reader = new CatalogReader(db, TimeProvider.System);
        var before = (await reader.GetEntriesAsync("08", new DateOnly(2026, 12, 31), CancellationToken.None)).Value;
        var after = (await reader.GetEntriesAsync("08", new DateOnly(2027, 1, 1), CancellationToken.None)).Value;

        Assert.Contains(before, e => e.Code == "03" && e.Version == 1);
        Assert.DoesNotContain(before, e => e.Code == "04");
        Assert.Equal(["01", "04"], after.Select(e => e.Code).ToArray());
        Assert.All(after, e => Assert.Equal(2, e.Version));
        Assert.True(await reader.IsValidCodeAsync("08", "04", new DateOnly(2027, 6, 1), CancellationToken.None));
        Assert.False(await reader.IsValidCodeAsync("08", "04", new DateOnly(2026, 6, 1), CancellationToken.None));

    }
}
