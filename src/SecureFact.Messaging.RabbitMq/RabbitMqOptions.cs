namespace SecureFact.Messaging.RabbitMq;

/// <summary>
/// Connection to the RabbitMQ broker (section <c>RabbitMq</c>). The password comes from the environment or a secret store, never from a file in the repository, and it is never logged.
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    /// <summary>Broker host. Required: without it the platform does not publish to a bus at all.</summary>
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 5672;

    public string VirtualHost { get; set; } = "/";

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>TLS to the broker. Off only for a local broker in development; the host name must then match the certificate of the broker.</summary>
    public bool UseTls { get; set; }

    /// <summary>Topic exchange that receives every integration event; the routing key is the event type.</summary>
    public string Exchange { get; set; } = "securefact.events";

    /// <summary>How long a publication may take, connection included, before it counts as failed and the outbox retries it.</summary>
    public int PublishTimeoutSeconds { get; set; } = 10;
}
