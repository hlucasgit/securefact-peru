using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SecureFact.Identity.Contracts;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;
using SecureFact.Rules;
using SecureFact.Rules.Application;
using SecureFact.Rules.Contracts;
using SecureFact.Rules.Infrastructure;

namespace SecureFact.Security.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class RulesTests(ApiFixture api)
{
    private static readonly string[] AllCodes = [RuleCodes.IgvRate, RuleCodes.IgvReducedRate, RuleCodes.IvapRate, RuleCodes.IcbperUnitAmount, RuleCodes.IssueDateMaxAgeDays, RuleCodes.ReceiptIdentificationThreshold];

    private RulesDbContext OwnerContext()
    {
        var scope = new DataScope();
        scope.UsePlatform("rules test");
        return new RulesDbContext(new DbContextOptionsBuilder<RulesDbContext>()
            .UseNpgsql(api.Postgres.OwnerConnectionString)
            .AddInterceptors(new RlsConnectionInterceptor(scope))
            .Options);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public async Task The_endpoint_lists_the_rules_in_force_with_their_verification_status()
    {
        var tenantId = await api.CreateTenantAsync("Rules Reader SAC");
        using var admin = await api.AdminClientAsync();
        var user = await ApiFixture.CreateUserAsync(admin, Roles.ReadOnly, tenantId);
        using var client = api.ClientFor(await api.LoginOkAsync(user.Email, user.Password));

        var rules = (await client.GetFromJsonAsync<List<RuleVersionDto>>("/api/v1/rules", ApiFixture.JsonOptions))!;

        Assert.Subset(rules.Select(r => r.Code).ToHashSet(StringComparer.Ordinal), AllCodes.ToHashSet(StringComparer.Ordinal));
        Assert.Equal(RuleVerification.Verified, rules.Single(r => r.Code == RuleCodes.IgvRate).Verification);
        Assert.Equal(RuleVerification.Pending, rules.Single(r => r.Code == RuleCodes.IcbperUnitAmount).Verification);
        Assert.Equal(RuleVerification.Verified, rules.Single(r => r.Code == RuleCodes.ReceiptIdentificationThreshold).Verification);
        Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Source)));
    }

    [Fact]
    public async Task Rules_require_authentication()
    {
        using var anonymous = api.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/rules")).StatusCode);
    }

    [Fact]
    public async Task The_runtime_role_cannot_change_a_rule()
    {
        await using var connection = new NpgsqlConnection(api.Postgres.AppConnectionString);
        await connection.OpenAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('app.scope', 'platform', false)", connection))
        {
            await scope.ExecuteNonQueryAsync();
        }

        await using var update = new NpgsqlCommand("UPDATE rules.rule_version SET source = 'tampered'", connection);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Resolution_returns_the_version_in_force_on_the_date_and_reads_numbers()
    {
        await using var db = OwnerContext();
        var provider = new RuleProvider(db);

        var igv = await provider.ResolveDecimalAsync(RuleCodes.IgvRate, "rate", new DateOnly(2026, 9, 30), CancellationToken.None);
        var window = await provider.ResolveDecimalAsync(RuleCodes.IssueDateMaxAgeDays, "01", new DateOnly(2026, 9, 30), CancellationToken.None);

        Assert.Equal(0.18m, igv.Value);
        Assert.Equal(3m, window.Value);
        Assert.Equal(RuleCodes.IgvRate, (await provider.ResolveAsync(RuleCodes.IgvRate, new DateOnly(1950, 1, 1), CancellationToken.None)).Value.Code);
    }

    [Fact]
    public async Task Unknown_rules_and_properties_are_explicit_errors()
    {
        await using var db = OwnerContext();
        var provider = new RuleProvider(db);

        var unknown = await provider.ResolveAsync("no.such.rule", new DateOnly(2026, 1, 1), CancellationToken.None);
        var noProperty = await provider.ResolveDecimalAsync(RuleCodes.IgvRate, "missing", new DateOnly(2026, 1, 1), CancellationToken.None);
        var tooEarly = await provider.ResolveAsync(RuleCodes.IgvRate, new DateOnly(1800, 1, 1), CancellationToken.None);

        Assert.Equal("SF-RUL-001", unknown.Error.Code);
        Assert.Equal("SF-RUL-002", noProperty.Error.Code);
        Assert.Equal("SF-RUL-001", tooEarly.Error.Code);
    }

    [Fact]
    public async Task A_published_rule_version_cannot_be_changed_in_place()
    {
        await using var db = OwnerContext();
        var tampered = new RuleSeeder.SeedFile("tamper", [new RuleSeeder.SeedRule(
            RuleCodes.IgvRate, 1, new DateOnly(1900, 1, 1), "x", RuleVerification.Verified, Json("{\"rate\":0.01}"))]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RuleSeeder.SeedAsync(db, CancellationToken.None, tampered));

        Assert.Contains("immutable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_version_takes_over_on_its_effective_date_and_closes_the_previous_one()
    {
        await using var db = OwnerContext();
        const string code = "test.rule.versioning";
        var seed = new RuleSeeder.SeedFile("t", [
            new RuleSeeder.SeedRule(code, 1, new DateOnly(2020, 1, 1), "s1", RuleVerification.Verified, Json("{\"rate\":0.10}")),
            new RuleSeeder.SeedRule(code, 2, new DateOnly(2027, 1, 1), "s2", RuleVerification.Verified, Json("{\"rate\":0.20}")),
        ]);

        await RuleSeeder.SeedAsync(db, CancellationToken.None, seed);
        await RuleSeeder.SeedAsync(db, CancellationToken.None, seed); // idempotent

        var provider = new RuleProvider(db);
        Assert.Equal(0.10m, (await provider.ResolveDecimalAsync(code, "rate", new DateOnly(2026, 12, 31), CancellationToken.None)).Value);
        Assert.Equal(0.20m, (await provider.ResolveDecimalAsync(code, "rate", new DateOnly(2027, 1, 1), CancellationToken.None)).Value);
        Assert.Equal("SF-RUL-001", (await provider.ResolveAsync(code, new DateOnly(2019, 12, 31), CancellationToken.None)).Error.Code);
    }

    [Fact]
    public async Task Reloading_the_embedded_seed_changes_nothing()
    {
        const string countSql = "SELECT count(*) FROM rules.rule_version WHERE code LIKE 'tax.%' OR code LIKE 'billing.%'";
        var before = await api.Postgres.ScalarAsOwnerAsync<long>(countSql);

        await RulesModule.MigrateAsync(api.Postgres.OwnerConnectionString);

        Assert.Equal(before, await api.Postgres.ScalarAsOwnerAsync<long>(countSql));
    }
}
