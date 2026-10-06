using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SecureFact.SharedKernel.Messaging;

namespace SecureFact.Messaging.RabbitMq;

public static class RabbitMqServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMessageBus"/> over RabbitMQ with the settings of <paramref name="section"/> (<c>RabbitMq</c>). The settings are checked when the host starts, so a
    /// wrong configuration stops it there rather than failing every publication later.
    /// </summary>
    public static IServiceCollection AddRabbitMqMessageBus(this IServiceCollection services, IConfiguration section)
    {
        services.AddOptions<RabbitMqOptions>()
            .Bind(section)
            .Validate(o => !string.IsNullOrWhiteSpace(o.Host), "RabbitMq:Host is required.")
            .Validate(o => o.Port is > 0 and <= 65535, "RabbitMq:Port must be a valid port.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.UserName) && !string.IsNullOrEmpty(o.Password), "RabbitMq:UserName and RabbitMq:Password are required (from the environment or a secret store).")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Exchange), "RabbitMq:Exchange is required.")
            .ValidateOnStart();
        services.AddSingleton<IMessageBus, RabbitMqMessageBus>();
        return services;
    }
}
