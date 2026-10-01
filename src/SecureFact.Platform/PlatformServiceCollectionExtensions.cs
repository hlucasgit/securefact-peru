using Microsoft.Extensions.DependencyInjection;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Tenancy;

namespace SecureFact.Platform;

public static class PlatformServiceCollectionExtensions
{
    /// <summary>Registers the per-request/per-message data scope that drives tenant isolation.</summary>
    public static IServiceCollection AddPlatformDataScope(this IServiceCollection services)
    {
        services.AddScoped<DataScope>();
        services.AddScoped<IDataScope>(sp => sp.GetRequiredService<DataScope>());
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<DataScope>());
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
