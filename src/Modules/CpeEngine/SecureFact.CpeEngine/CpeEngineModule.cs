using Microsoft.Extensions.DependencyInjection;
using SecureFact.CpeEngine.Contracts;

namespace SecureFact.CpeEngine;

public static class CpeEngineModule
{
    /// <summary>Stateless services of the CPE engine (QR payload today; UBL, signing and CDR parsing arrive in Phase 3).</summary>
    public static IServiceCollection AddCpeEngineModule(this IServiceCollection services) =>
        services
            .AddSingleton<IQrPayloadGenerator, QrPayloadGenerator>()
            .AddSingleton<IUblDocumentGenerator, UblInvoiceGenerator>()
            .AddSingleton<IXmlSigner, XmlDsigSigner>()
            .AddSingleton<ICpePackager, ZipCpePackager>()
            .AddSingleton<ICdrParser, CdrParser>()
            .AddSingleton<IEDocumentStateMachine, EDocumentStateMachine>();

    /// <summary>Registers the SOAP channel to SUNAT. The endpoint is explicit: production and beta are never chosen implicitly.</summary>
    public static IServiceCollection AddSunatSubmissionChannel(this IServiceCollection services, SunatChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddHttpClient<ICpeSubmissionChannel, SunatSoapChannel>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        return services;
    }
}
