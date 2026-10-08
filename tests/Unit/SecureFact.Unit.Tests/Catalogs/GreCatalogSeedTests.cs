using SecureFact.CatalogImporter;

namespace SecureFact.Unit.Tests.Catalogs;

/// <summary>The catalogues of the GRE come from the workbook of the GRE (S27, 25.09.2026), which is newer than the one of the CPE for them: motives of transfer, modalities, related documents, ports, airports and units.</summary>
public class GreCatalogSeedTests
{
    private static readonly HashSet<string> Kept = new(["18", "20", "61", "63", "64", "65"], StringComparer.Ordinal);

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
        CatalogWorkbookParser.Parse(Path.Combine(RepoRoot(), "docs", "regulatory", "assets", "gre", "reglas-validacion-publicado-2026-09-25.xlsx"), Kept));

    private static CatalogSeed Catalog(string number) => Parsed.Value.Catalogs.Single(c => c.Number == number);

    [Fact]
    public void The_stored_workbook_has_the_hash_recorded_in_the_provenance_file()
    {
        var sums = File.ReadAllLines(Path.Combine(RepoRoot(), "docs", "regulatory", "assets", "SHA256SUMS.txt"));

        var line = Assert.Single(sums, l => l.EndsWith("reglas-validacion-publicado-2026-09-25.xlsx", StringComparison.Ordinal));
        Assert.StartsWith(Parsed.Value.Source.Sha256, line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_committed_seed_is_exactly_what_the_importer_produces_from_the_stored_workbook()
    {
        var committed = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Modules", "Catalogs", "SecureFact.Catalogs", "Seeds", "sunat-catalogs-gre-2026-09-25.json"));

        Assert.Equal(CatalogWorkbookParser.ToJson(Parsed.Value) + "\n", committed.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Only_the_catalogues_of_the_gre_are_kept()
    {
        Assert.Equal(Kept.Order(StringComparer.Ordinal), Parsed.Value.Catalogs.Select(c => c.Number).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_motives_of_transfer_are_the_fourteen_of_the_workbook()
    {
        Assert.Equal(["01", "02", "03", "04", "05", "06", "07", "08", "09", "13", "14", "17", "18", "19"], Catalog("20").Entries.Select(e => e.Code));
        Assert.Equal("Venta", Catalog("20").Entries.Single(e => e.Code == "01").Description);
        Assert.Equal("Importación", Catalog("20").Entries.Single(e => e.Code == "08").Description);
    }

    [Fact]
    public void The_modalities_are_public_and_private()
    {
        Assert.Equal([("01", "Transporte público"), ("02", "Transporte privado")], Catalog("18").Entries.Select(e => (e.Code, e.Description)));
    }

    [Fact]
    public void The_related_documents_say_for_which_guide_each_one_applies()
    {
        var entries = Catalog("61").Entries.ToDictionary(e => e.Code);

        Assert.Equal("remitente, transportista", entries["01"].Extra["GRE Aplicable"]);
        Assert.Equal("solo transportista", entries["31"].Extra["GRE Aplicable"]);
        Assert.Equal("solo remitente", entries["49"].Extra["GRE Aplicable"]);
        Assert.Contains("91", entries.Keys);
    }

    [Fact]
    public void The_ports_and_airports_carry_their_ubigeo()
    {
        Assert.All(Catalog("63").Entries, e => Assert.True(e.Extra.ContainsKey("Ubigeo"), e.Code));
        Assert.All(Catalog("64").Entries, e => Assert.True(e.Extra.ContainsKey("Ubigeo"), e.Code));
    }
}
