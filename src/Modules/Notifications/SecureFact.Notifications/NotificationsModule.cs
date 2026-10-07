using Microsoft.Extensions.DependencyInjection;
using SecureFact.Notifications.Contracts;

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

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }
    }
}
