using Microsoft.Extensions.DependencyInjection;
using SecureFact.CpeEngine.Contracts;

namespace SecureFact.CpeEngine;

public static class CpeEngineModule
{
    /// <summary>Stateless services of the CPE engine (QR payload today; UBL, signing and CDR parsing arrive in Phase 3).</summary>
    public static IServiceCollection AddCpeEngineModule(this IServiceCollection services) =>
        services.AddSingleton<IQrPayloadGenerator, QrPayloadGenerator>();
}
