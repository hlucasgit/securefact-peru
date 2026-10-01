using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecureFact.CpeEngine.Application;
using SecureFact.CpeEngine.Contracts;
using SecureFact.CpeEngine.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;

namespace SecureFact.CpeEngine;

public static class CpeEngineModule
{
    /// <summary>Stateless services of the CPE engine (QR payload, UBL, signing, packaging, CDR parsing, lifecycle rules).</summary>
    public static IServiceCollection AddCpeEngineModule(this IServiceCollection services) =>
        services
            .AddSingleton<IQrPayloadGenerator, QrPayloadGenerator>()
            .AddSingleton<IUblDocumentGenerator, UblInvoiceGenerator>()
            .AddSingleton<ISummaryDocumentGenerator, SummaryDocumentGenerator>()
            .AddSingleton<IXmlSigner, XmlDsigSigner>()
            .AddSingleton<ICpePackager, ZipCpePackager>()
            .AddSingleton<ICdrParser, CdrParser>()
            .AddSingleton<IEDocumentStateMachine, EDocumentStateMachine>();

    /// <summary>
    /// The electronic-document pipeline (prepare, sign, send, record the CDR). Requires <c>AddPlatformDataScope</c>, the Audit, Billing,
    /// Organizations, Rules and Certificates modules and <see cref="AddCpeEngineModule"/>. Sending also needs <see cref="AddSunatSubmissionChannel"/>.
    /// </summary>
    public static IServiceCollection AddCpePipeline(this IServiceCollection services, string appConnectionString)
    {
        services.AddDbContext<CpeDbContext>((sp, options) => options
            .UseNpgsql(appConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", CpeDbContext.Schema))
            .AddInterceptors(new RlsConnectionInterceptor(sp.GetRequiredService<IDataScope>())));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IElectronicDocumentService, ElectronicDocumentService>();
        return services;
    }

    /// <summary>Registers the SOAP channel to SUNAT. The endpoint is explicit: production and beta are never chosen implicitly.</summary>
    public static IServiceCollection AddSunatSubmissionChannel(this IServiceCollection services, SunatChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddHttpClient<ICpeSubmissionChannel, SunatSoapChannel>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        return services;
    }

    /// <summary>Applies pending migrations. Must run with the schema-owner connection, never the runtime role.</summary>
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CpeDbContext>()
            .UseNpgsql(ownerConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", CpeDbContext.Schema))
            .Options;
        await using var db = new CpeDbContext(options, new DataScope());
        await db.Database.MigrateAsync(cancellationToken);
    }
}
