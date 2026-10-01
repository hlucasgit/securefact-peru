using System.Text.Json;
using SecureFact.Billing.Application;
using SecureFact.CatalogImporter;
using SecureFact.TaxEngine;

namespace SecureFact.Unit.Tests.Catalogs;

public class CatalogSeedTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SecureFact.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static readonly Lazy<CatalogSeedFile> Parsed = new(() =>
        CatalogWorkbookParser.Parse(Path.Combine(RepoRoot(), "docs", "regulatory", "assets", "reglas-de-validacion-2026-08-26.xlsx")));

    private static readonly string[] RequiredDocumentTypes = ["01", "03", "07", "08"];
    private static readonly string[] RequiredLegends = ["1000", "1002", "2006"];

    private static CatalogSeed Catalog(string number) => Parsed.Value.Catalogs.Single(c => c.Number == number);

    [Fact]
    public void The_stored_workbook_has_the_hash_recorded_in_the_provenance_file()
    {
        var sums = File.ReadAllLines(Path.Combine(RepoRoot(), "docs", "regulatory", "assets", "SHA256SUMS.txt"));

        var line = Assert.Single(sums, l => l.EndsWith("reglas-de-validacion-2026-08-26.xlsx", StringComparison.Ordinal));
        Assert.StartsWith(Parsed.Value.Source.Sha256, line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_committed_seed_is_exactly_what_the_importer_produces_from_the_stored_workbook()
    {
        var committed = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Modules", "Catalogs", "SecureFact.Catalogs", "Seeds", "sunat-catalogs-2026-08-26.json"));

        Assert.Equal(CatalogWorkbookParser.ToJson(Parsed.Value) + "\n", committed.Replace("\r\n", "\n"));
    }

    [Fact]
    public void The_workbook_yields_the_official_catalogue_set()
    {
        var numbers = Parsed.Value.Catalogs.Select(c => c.Number).ToList();

        Assert.Equal(numbers.Count, numbers.Distinct().Count());
        Assert.True(numbers.Count >= 40);
        foreach (var required in new[] { "01", "05", "06", "07", "08", "09", "10", "52", "53", "54", "59" })
        {
            Assert.Contains(required, numbers);
        }

        Assert.All(Parsed.Value.Catalogs, c => Assert.All(c.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Code))));
    }

    [Fact]
    public void Tax_engine_affectation_codes_match_catalogue_07_exactly()
    {
        var official = Catalog("07").Entries.Select(e => e.Code).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(official, TaxCalculator.SupportedAffectationCodes.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Tax_codes_used_by_the_engine_exist_in_catalogue_05()
    {
        var official = Catalog("05").Entries.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);

        foreach (var code in new[] { "1000", "1016", "2000", "7152", "9995", "9996", "9997", "9998", "9999" })
        {
            Assert.Contains(code, official);
        }

        var igv = Catalog("05").Entries.Single(e => e.Code == "1000");
        Assert.Equal("VAT", igv.Extra["Código internacional"]);
    }

    [Fact]
    public void Buyer_identity_types_accepted_by_billing_exist_in_catalogue_06()
    {
        var official = Catalog("06").Entries.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);

        Assert.All(BillingRules.SupportedBuyerDocumentTypes, code => Assert.Contains(code, official));
    }

    [Fact]
    public void Document_types_and_legends_used_by_the_platform_exist()
    {
        var types = Catalog("01").Entries.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);
        var legends = Catalog("52").Entries.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);

        Assert.All(RequiredDocumentTypes, code => Assert.Contains(code, types));
        Assert.All(RequiredLegends, code => Assert.Contains(code, legends));
    }

    [Fact]
    public void Seed_json_round_trips_without_losing_entries()
    {
        using var doc = JsonDocument.Parse(CatalogWorkbookParser.ToJson(Parsed.Value));

        Assert.Equal(Parsed.Value.Catalogs.Count, doc.RootElement.GetProperty("Catalogs").GetArrayLength());
    }
}
