using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecureFact.Identity.Contracts;
using SecureFact.Notifications.Application;
using SecureFact.Notifications.Contracts;
using SecureFact.Notifications.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.Platform.Tenancy;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Notifications;

public static class NotificationsModule
{
    /// <summary>
    /// Registers the outgoing e-mail (ADR-052). <c>Sandbox</c> (files on disk) and an SMTP connection without TLS are for development and tests, and are refused in production.
    /// </summary>
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, EmailOptions options, bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        switch (options.Provider)
        {
            case EmailOptions.ProviderNone:
                services.AddSingleton<IEmailSender, UnconfiguredEmailSender>();
                break;
            case EmailOptions.ProviderSmtp:
                Require(options.Smtp.Host, "Email:Smtp:Host");
                Require(options.From, "Email:From");
                if (options.Smtp.Security is not ("StartTls" or "Tls" or "None"))
                {
                    throw new InvalidOperationException("Email:Smtp:Security must be 'StartTls', 'Tls' or 'None'.");
                }

                if (isProduction && options.Smtp.Security == "None")
                {
                    throw new InvalidOperationException("Email:Smtp:Security 'None' (no encryption) is not allowed in production.");
                }

                services.AddSingleton<IEmailSender, SmtpEmailSender>();
                break;
            case EmailOptions.ProviderSandbox:
                if (isProduction)
                {
                    throw new InvalidOperationException("Email:Provider 'Sandbox' (e-mails written to disk) is not allowed in production.");
                }

                Require(options.Sandbox.Directory, "Email:Sandbox:Directory");
                Require(options.From, "Email:From");
                services.AddSingleton<IEmailSender, FileEmailSender>();
                break;
            default:
                throw new InvalidOperationException("Email:Provider must be 'None', 'Smtp' or 'Sandbox'.");
        }

        return services;
    }

    /// <summary>
    /// The address of the web interface and the brand that signs the e-mails (ADR-052, ADR-054). Needs <see cref="IBranding"/> (the Tenancy module). Production requires an absolute
    /// <c>https</c> address: it is the base of every link that goes out.
    /// </summary>
    public static IServiceCollection AddPortalLinks(this IServiceCollection services, WebOptions web, bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(web);
        if (isProduction && !(Uri.TryCreate(web.PublicUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Web:PublicUrl is required in production and must be an https address: it is the base of the links that go out in e-mails.");
        }

        services.AddSingleton(web);
        services.AddScoped<NoticeContext>();
        return services;
    }

    /// <summary>
    /// The queue of e-mails with its dispatcher, and the notices that use it: the state of an account, the domain of a reseller and the creation of an account (ADR-054). Needs
    /// <c>AddPlatformDataScope</c>, <see cref="AddNotificationsModule"/>, <see cref="AddPortalLinks"/>, the Tenancy module and an <see cref="IAccountDirectory"/> (the Identity module
    /// or its <c>AddAccountDirectory</c>). It replaces the silent defaults of the modules wherever it is registered.
    /// </summary>
    public static IServiceCollection AddEmailNotices(this IServiceCollection services, string appConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddDbContext<NotificationsDbContext>((sp, options) => options
            .UseNpgsql(appConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", NotificationsDbContext.Schema))
            .AddInterceptors(new RlsConnectionInterceptor(sp.GetRequiredService<IDataScope>())));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IEmailOutbox, EmailOutbox>();
        services.AddScoped<IEmailDispatcher, EmailDispatcher>();
        services.AddScoped<ICertificateExpiryNotices, CertificateExpiryNotices>();
        services.Replace(ServiceDescriptor.Scoped<IBusinessNotices, BusinessNoticeEmails>());
        services.Replace(ServiceDescriptor.Scoped<ITenantNotices, TenantNoticeEmails>());
        services.Replace(ServiceDescriptor.Scoped<IAccountNotices, AccountNoticeEmails>());
        return services;
    }

    /// <summary>Applies pending migrations. Must run with the schema-owner connection, never the runtime role.</summary>
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(ownerConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", NotificationsDbContext.Schema))
            .Options;
        await using var db = new NotificationsDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }
    }
}
