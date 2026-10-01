using System.Reflection;

namespace SecureFact.Architecture.Tests;

/// <summary>
/// Enforces ADR-001/ADR-010: a module may reference only SharedKernel, Platform and Contracts assemblies;
/// Contracts and SharedKernel stay at the bottom of the graph; hosts may reference anything.
/// </summary>
public class ModuleBoundaryTests
{
    private const string Prefix = "SecureFact.";
    private const string SharedKernel = "SecureFact.SharedKernel";
    private const string Platform = "SecureFact.Platform";
    private static readonly string[] Hosts = ["SecureFact.Api", "SecureFact.Workers"];

    private static IEnumerable<Assembly> LoadSolutionAssemblies() =>
        Directory.GetFiles(AppContext.BaseDirectory, "SecureFact.*.dll")
            .Where(f => !Path.GetFileName(f).Contains("Tests", StringComparison.Ordinal))
            .Select(Assembly.LoadFrom);

    private static IEnumerable<string> SolutionReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal));

    private static bool IsContracts(string name) => name.EndsWith(".Contracts", StringComparison.Ordinal);

    [Fact]
    public void Solution_assemblies_are_discovered()
    {
        var names = LoadSolutionAssemblies().Select(a => a.GetName().Name).ToList();

        Assert.Contains(SharedKernel, names);
        Assert.Contains("SecureFact.Api", names);
    }

    [Fact]
    public void SharedKernel_references_no_other_solution_assembly()
    {
        var shared = LoadSolutionAssemblies().Single(a => a.GetName().Name == SharedKernel);

        Assert.Empty(SolutionReferences(shared));
    }

    [Fact]
    public void Platform_references_only_SharedKernel()
    {
        var platform = LoadSolutionAssemblies().Single(a => a.GetName().Name == Platform);

        var illegal = SolutionReferences(platform).Where(n => n != SharedKernel).ToList();
        Assert.True(illegal.Count == 0, $"Platform references {string.Join(", ", illegal)}");
    }

    [Fact]
    public void Contracts_reference_only_SharedKernel_and_other_Contracts()
    {
        foreach (var contracts in LoadSolutionAssemblies().Where(a => IsContracts(a.GetName().Name!)))
        {
            var illegal = SolutionReferences(contracts).Where(n => n != SharedKernel && !IsContracts(n)).ToList();
            Assert.True(illegal.Count == 0, $"{contracts.GetName().Name} references {string.Join(", ", illegal)}");
        }
    }

    [Fact]
    public void Modules_reference_only_SharedKernel_and_other_modules_Contracts()
    {
        var modules = LoadSolutionAssemblies()
            .Where(a => a.GetName().Name is { } n && n != SharedKernel && n != Platform && !IsContracts(n) && !Hosts.Contains(n));

        foreach (var module in modules)
        {
            var own = module.GetName().Name!;
            var illegal = SolutionReferences(module)
                .Where(n => n != SharedKernel && n != Platform && !IsContracts(n))
                .ToList();
            Assert.True(illegal.Count == 0, $"{own} references assemblies other than SharedKernel, Platform or Contracts: {string.Join(", ", illegal)}");
        }
    }

    [Fact]
    public void Public_money_like_members_never_use_floating_point()
    {
        // Contexto §15: jamás float/double para valores monetarios. Guard against regressions in public contract surface.
        var offenders = LoadSolutionAssemblies()
            .SelectMany(a => a.GetExportedTypes())
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(p => (p.PropertyType == typeof(double) || p.PropertyType == typeof(float))
                        && (p.Name.Contains("Amount", StringComparison.OrdinalIgnoreCase)
                            || p.Name.Contains("Price", StringComparison.OrdinalIgnoreCase)
                            || p.Name.Contains("Total", StringComparison.OrdinalIgnoreCase)
                            || p.Name.Contains("Tax", StringComparison.OrdinalIgnoreCase)))
            .Select(p => $"{p.DeclaringType!.FullName}.{p.Name}")
            .ToList();

        Assert.Empty(offenders);
    }
}
