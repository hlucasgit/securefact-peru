using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using SecureFact.Messaging.RabbitMq;
using SecureFact.SharedKernel.Messaging;
using Testcontainers.RabbitMq;

namespace SecureFact.Security.Tests;

/// <summary>One real RabbitMQ per test run (ADR-035), with credentials that exist only in the container.</summary>
public sealed class RabbitFixture : IAsyncLifetime
{
    public const string User = "securefact-test";
    public const string Password = "rabbit-test-password";

    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4").WithUsername(User).WithPassword(Password).Build();

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(5672);

    public async Task InitializeAsync() => await _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public RabbitMqOptions Options(string? exchange = null) => new()
    {
        Host = Host,
        Port = Port,
        UserName = User,
        Password = Password,
        Exchange = exchange ?? $"test.events.{Guid.NewGuid():N}",
        PublishTimeoutSeconds = 15,
    };

    public async Task CloseAllConnectionsAsync() =>
        await _container.ExecAsync(["rabbitmqctl", "close_all_connections", "test"]);

    /// <summary>A queue bound to the exchange with the given pattern, read by hand with <c>basic.get</c>.</summary>
    public async Task<Subscription> SubscribeAsync(string exchange, string routingPattern)
    {
        var connection = await new ConnectionFactory { HostName = Host, Port = Port, UserName = User, Password = Password }.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true);
        var queue = (await channel.QueueDeclareAsync(queue: string.Empty, durable: false, exclusive: true, autoDelete: true)).QueueName;
        await channel.QueueBindAsync(queue, exchange, routingPattern);
        return new Subscription(connection, channel, queue);
    }
}

public sealed class Subscription(IConnection connection, IChannel channel, string queue) : IAsyncDisposable
{
    public async Task<BasicGetResult?> NextAsync(TimeSpan? wait = null)
    {
        var deadline = DateTimeOffset.UtcNow + (wait ?? TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await channel.BasicGetAsync(queue, autoAck: true);
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(100);
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await channel.DisposeAsync();
        await connection.DisposeAsync();
    }
}

[Collection(ApiTestGroup.Name)]
public sealed class MessageBusTests(RabbitFixture rabbit)
{
    private static RabbitMqMessageBus NewBus(RabbitMqOptions options) => new(Microsoft.Extensions.Options.Options.Create(options), NullLogger<RabbitMqMessageBus>.Instance);

    private static BusMessage Message(string type = "billing.document.issued", string payload = "{\"documentId\":\"4d6f\"}") =>
        new(Guid.NewGuid(), Guid.NewGuid(), type, payload, DateTimeOffset.UtcNow);

    private static string Header(IReadOnlyBasicProperties properties, string name) => Encoding.UTF8.GetString((byte[])properties.Headers![name]!);

    [Fact]
    public async Task An_event_reaches_the_subscribers_of_its_routing_key_as_a_persistent_json_message_carrying_the_outbox_id()
    {
        var options = rabbit.Options();
        await using var bus = NewBus(options);
        await using var billing = await rabbit.SubscribeAsync(options.Exchange, "billing.#");
        await using var other = await rabbit.SubscribeAsync(options.Exchange, "customers.#");
        var message = Message();

        await bus.PublishAsync(message, CancellationToken.None);

        var received = await billing.NextAsync();
        Assert.NotNull(received);
        Assert.Equal("billing.document.issued", received.RoutingKey);
        Assert.Equal(message.PayloadJson, Encoding.UTF8.GetString(received.Body.ToArray()));
        Assert.Equal(message.MessageId.ToString("D"), received.BasicProperties.MessageId);
        Assert.Equal("application/json", received.BasicProperties.ContentType);
        Assert.Equal(DeliveryModes.Persistent, received.BasicProperties.DeliveryMode);
        Assert.Equal("billing.document.issued", received.BasicProperties.Type);
        Assert.Equal(message.TenantId.ToString("D"), Header(received.BasicProperties, RabbitMqMessageBus.TenantHeader));
        Assert.Equal("billing.document.issued", Header(received.BasicProperties, RabbitMqMessageBus.EventTypeHeader));
        Assert.Null(await other.NextAsync(TimeSpan.FromSeconds(1))); // a subscriber of another key sees nothing
    }

    [Fact]
    public async Task A_publication_the_broker_cannot_take_throws_so_that_the_outbox_retries_it()
    {
        var unreachable = rabbit.Options();
        unreachable.Port = 1; // nobody listens
        unreachable.PublishTimeoutSeconds = 5;
        await using var bus = NewBus(unreachable);

        await Assert.ThrowsAnyAsync<Exception>(() => bus.PublishAsync(Message(), CancellationToken.None));

        var wrongPassword = rabbit.Options();
        wrongPassword.Password = "not-the-password";
        await using var refused = NewBus(wrongPassword);
        await Assert.ThrowsAnyAsync<Exception>(() => refused.PublishAsync(Message(), CancellationToken.None));
    }

    [Fact]
    public async Task A_cancellation_by_the_caller_is_not_swallowed()
    {
        await using var bus = NewBus(rabbit.Options());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bus.PublishAsync(Message(), cancelled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => bus.PublishAsync(Message(type: " "), CancellationToken.None));
    }

    [Fact]
    public async Task After_the_broker_drops_the_connection_the_next_attempts_open_a_new_one()
    {
        var options = rabbit.Options();
        await using var bus = NewBus(options);
        await using var subscription = await rabbit.SubscribeAsync(options.Exchange, "#");
        await bus.PublishAsync(Message(), CancellationToken.None);
        Assert.NotNull(await subscription.NextAsync());

        await rabbit.CloseAllConnectionsAsync();

        // The publication that finds the connection dead may fail (the outbox would retry it); the ones after it must get through.
        var delivered = false;
        for (var attempt = 0; attempt < 5 && !delivered; attempt++)
        {
            try
            {
                await bus.PublishAsync(Message(type: "billing.after.reconnect"), CancellationToken.None);
                delivered = true;
            }
            catch (Exception)
            {
                await Task.Delay(500);
            }
        }

        Assert.True(delivered);
    }
}
