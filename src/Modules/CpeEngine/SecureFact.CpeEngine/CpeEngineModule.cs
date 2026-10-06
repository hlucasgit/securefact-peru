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
            .AddSingleton<IVoidedDocumentsGenerator, VoidedDocumentsGenerator>()
            .AddSingleton<IPrintedRepresentationRenderer, Printing.PdfPrintedRepresentationRenderer>()
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
        services.AddScoped<ISummaryService, SummaryService>();
        services.AddScoped<IVoidService, VoidService>();
        services.AddScoped<SecureFact.Billing.Contracts.IVoidStatusProvider, VoidStatusProvider>();
        services.AddScoped<SecureFact.Billing.Contracts.IIneffectiveDocumentsProvider, IneffectiveDocumentsProvider>();
        services.AddScoped<SecureFact.SharedKernel.Messaging.IIntegrationEventConsumer, DocumentIssuedHandler>();

        // The archive in object storage (ADR-036). The host registers the IObjectStorage (S3, or the in-memory one outside production); the events of the CPE engine travel through its own outbox.
        services.AddScoped<DocumentArchiver>();
        services.AddScoped<IDocumentArchive>(sp => sp.GetRequiredService<DocumentArchiver>());
        services.AddScoped<SecureFact.SharedKernel.Messaging.IIntegrationEventConsumer>(sp => new ArchiveConsumer(CpeEvents.DocumentPrepared, ArchiveKinds.SignedXml, sp.GetRequiredService<DocumentArchiver>()));
        services.AddScoped<SecureFact.SharedKernel.Messaging.IIntegrationEventConsumer>(sp => new ArchiveConsumer(CpeEvents.DocumentAnswered, ArchiveKinds.CdrZip, sp.GetRequiredService<DocumentArchiver>()));
        services.AddScoped<SecureFact.SharedKernel.Messaging.IOutboxSource>(sp => new SecureFact.Platform.Messaging.PostgresOutboxSource(sp.GetRequiredService<CpeDbContext>(), sp.GetRequiredService<TimeProvider>(), CpeDbContext.Schema, "cpe"));
        services.AddSingleton<ICpeWorkProcessor, CpeWorkProcessor>();
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

    /// <summary>
    /// Registers the in-process SUNAT simulator (ADR-039) for development, demos and end-to-end tests. It is refused in production: it accepts documents without validating them, so a real
    /// issuer would believe its documents were accepted by SUNAT.
    /// </summary>
    public static IServiceCollection AddSandboxSubmissionChannel(this IServiceCollection services, bool isProduction)
    {
        if (isProduction)
        {
            throw new InvalidOperationException("Sunat:Environment 'Sandbox' (the SUNAT simulator) is not allowed in production.");
        }

        services.AddSingleton<ICpeSubmissionChannel, SandboxSunatChannel>();
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
