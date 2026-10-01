using SecureFact.CatalogImporter;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: SecureFact.CatalogImporter <reglas-de-validacion.xlsx> <output.json>");
    return 2;
}

var seed = CatalogWorkbookParser.Parse(args[0]);
File.WriteAllText(args[1], CatalogWorkbookParser.ToJson(seed) + "\n");
Console.WriteLine($"{seed.Catalogs.Count} catalogues, {seed.Catalogs.Sum(c => c.Entries.Count)} entries -> {args[1]}");
return 0;
