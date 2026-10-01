using Microsoft.Extensions.DependencyInjection;
using SecureFact.TaxEngine.Contracts;

namespace SecureFact.TaxEngine;

public static class TaxEngineModule
{
    /// <summary>The calculator is stateless and thread-safe, so it is a singleton.</summary>
    public static IServiceCollection AddTaxEngineModule(this IServiceCollection services) =>
        services.AddSingleton<ITaxCalculator, TaxCalculator>();
}
