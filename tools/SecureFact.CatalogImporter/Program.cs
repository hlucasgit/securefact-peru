using SecureFact.CatalogImporter;

if (args.Length is not (2 or 4) || (args.Length == 4 && args[2] != "--only"))
{
    Console.Error.WriteLine("Usage: SecureFact.CatalogImporter <reglas-de-validacion.xlsx> <output.json> [--only 18,20,61]");
    return 2;
}

var only = args.Length == 4 ? args[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal) : null;
var seed = CatalogWorkbookParser.Parse(args[0], only);
File.WriteAllText(args[1], CatalogWorkbookParser.ToJson(seed) + "\n");
Console.WriteLine($"{seed.Catalogs.Count} catalogues, {seed.Catalogs.Sum(c => c.Entries.Count)} entries -> {args[1]}");
return 0;
