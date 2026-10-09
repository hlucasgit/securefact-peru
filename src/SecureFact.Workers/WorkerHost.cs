using SecureFact.Audit;
using SecureFact.Billing;
using SecureFact.Catalogs;
using SecureFact.Certificates;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Customers;
using SecureFact.Gre;
using SecureFact.Gre.Contracts;
using SecureFact.Subscriptions;
using SecureFact.Identity;
using SecureFact.Messaging.RabbitMq;
using SecureFact.Notifications;
using SecureFact.Organizations;
using SecureFact.Platform;
using SecureFact.Platform.Messaging;
using SecureFact.Platform.Security;
using SecureFact.Platform.Storage;
using SecureFact.Rules;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Storage.S3;
using SecureFact.TaxEngine;
using SecureFact.Tenancy;
using SecureFact.Workers.Infrastructure;

namespace SecureFact.Workers;

/// <summary>Composition of the worker host; shared by <c>Program</c> and the tests so that both run the real wiring.</summary>
internal static class WorkerHost
{
    public static HostApplicationBuilder Create(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        var appConnection = builder.Configuration.GetConnectionString("App")
            ?? throw new InvalidOperationException("ConnectionStrings:App is required (the runtime role, never the migration owner).");

        if (!builder.Environment.IsDevelopment())
        {
            builder.Logging.AddJsonConsole();
        }

        builder.Services.AddPlatformDataScope();
        builder.Services.AddSingleton<ISecretProtector>(_ =>
        {
            // ADR-007: the in-process KEK is for development and tests only. Production needs a KMS/Vault-backed protector.
            if (builder.Environment.IsProduction())
            {
                throw new InvalidOperationException("No production ISecretProtector is configured. Register a KMS/Vault-backed implementation (ADR-007).");
            }

            return LocalEnvelopeSecretProtector.FromBase64(builder.Configuration["Security:LocalDevKek"]);
        });
        // Documents are archived in object storage (ADR-005, ADR-036): S3 when a bucket is configured. Without one only a development machine or a test may use the in-memory storage.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["Storage:S3:Bucket"]))
        {
            builder.Services.AddS3ObjectStorage(builder.Configuration.GetSection(S3StorageOptions.SectionName));
        }
        else if (builder.Environment.IsProduction())
        {
            throw new InvalidOperationException("Storage:S3:Bucket is required in production: the documents cannot be archived in memory (ADR-005).");
        }
        else
        {
            builder.Services.AddInMemoryObjectStorage();
        }

        builder.Services.AddScoped<ICurrentUser, SystemCurrentUser>();
        builder.Services.AddScoped<IRequestContext, WorkerRequestContext>();

        builder.Services.AddAuditModule(appConnection);
        builder.Services.AddTenancyModule(appConnection);
        builder.Services.AddDomainProvisioning(builder.Configuration.GetSection(DomainsOptions.SectionName).Get<DomainsOptions>() ?? new DomainsOptions(), builder.Environment.IsProduction());
        // The notices (ADR-054): the queue of e-mails is sent from here, and the notices of the domains are queued from here. The workers sign nobody in, so they only need the directory of who to write to.
        builder.Services.AddNotificationsModule(builder.Configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new EmailOptions(), builder.Environment.IsProduction());
        builder.Services.AddPortalLinks(builder.Configuration.GetSection(WebOptions.SectionName).Get<WebOptions>() ?? new WebOptions(), builder.Environment.IsProduction());
        builder.Services.AddAccountDirectory(appConnection);
        builder.Services.AddEmailNotices(appConnection);
        builder.Services.AddOrganizationsModule(appConnection);
        builder.Services.AddTaxEngineModule();
        builder.Services.AddCatalogsModule(appConnection);
        builder.Services.AddRulesModule(appConnection);
        builder.Services.AddCustomersModule(appConnection);
        builder.Services.AddBillingModule(appConnection);
        builder.Services.AddCertificatesModule(appConnection);
        builder.Services.AddCpeEngineModule();
        builder.Services.AddCpePipeline(appConnection);
        builder.Services.AddGreModule(appConnection);
        builder.Services.AddSubscriptionsModule(appConnection);

        // SUNAT is never reached implicitly: the environment must be named. Without it the worker only prepares summaries.
        switch (builder.Configuration["Sunat:Environment"])
        {
            case "Beta":
                builder.Services.AddSunatSubmissionChannel(SunatChannelOptions.Beta);
                break;
            case "Production":
                builder.Services.AddSunatSubmissionChannel(SunatChannelOptions.Production);
                builder.Services.AddGreSubmissionChannel(GreChannelOptions.Production);
                break;
            case "Sandbox":
                // The in-process simulator (ADR-039): for development and end-to-end tests, never production.
                builder.Services.AddSandboxSubmissionChannel(builder.Environment.IsProduction());
                builder.Services.AddSandboxGreChannel(builder.Environment.IsProduction());
                break;
            case null or "":
                break;
            default:
                throw new InvalidOperationException("Sunat:Environment must be 'Beta', 'Production' or 'Sandbox'.");
        }

        builder.Services.AddOutboxProcessing();

        // Events leave for the message bus only when a broker is configured (ADR-035); without RabbitMq:Host the outbox delivers to the platform's own consumers and nothing else.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["RabbitMq:Host"]))
        {
            builder.Services.AddRabbitMqMessageBus(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
            builder.Services.AddBusPublishing();
        }

        builder.Services.AddHostedService<OutboxWorker>();
        builder.Services.AddHostedService<CpeWorker>();
        builder.Services.AddHostedService<GreWorker>();
        builder.Services.AddHostedService<SubscriptionWorker>();
        builder.Services.AddHostedService<DomainWorker>();
        builder.Services.AddHostedService<EmailWorker>();

        return builder;
    }
}
