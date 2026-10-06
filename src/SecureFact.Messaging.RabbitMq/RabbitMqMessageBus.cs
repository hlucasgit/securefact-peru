using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using SecureFact.SharedKernel.Messaging;

namespace SecureFact.Messaging.RabbitMq;

/// <summary>
/// <see cref="IMessageBus"/> over RabbitMQ (ADR-035). Events go to one durable topic exchange, as persistent messages, with the event type as routing key and the outbox message id as
/// <c>message-id</c>. Publisher confirms are on: <see cref="PublishAsync"/> returns only after the broker took responsibility for the message and throws when it did not, so the outbox
/// retries instead of losing it. One connection is opened on first use and rebuilt after a failure.
/// </summary>
public sealed partial class RabbitMqMessageBus(IOptions<RabbitMqOptions> options, ILogger<RabbitMqMessageBus> logger) : IMessageBus, IAsyncDisposable
{
    public const string TenantHeader = "tenant-id";
    public const string EventTypeHeader = "event-type";

    private readonly RabbitMqOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(BusMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(message.EventType))
        {
            throw new ArgumentException("The event type is the routing key and cannot be empty.", nameof(message));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(_options.PublishTimeoutSeconds, 1)));

        await _gate.WaitAsync(timeout.Token);
        try
        {
            var channel = await EnsureChannelAsync(timeout.Token);
            var properties = new BasicProperties
            {
                MessageId = message.MessageId.ToString("D"),
                Type = message.EventType,
                ContentType = "application/json",
                ContentEncoding = "utf-8",
                DeliveryMode = DeliveryModes.Persistent,
                Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds()),
                Headers = new Dictionary<string, object?>
                {
                    [TenantHeader] = message.TenantId.ToString("D"),
                    [EventTypeHeader] = message.EventType,
                },
            };

            await channel.BasicPublishAsync(_options.Exchange, message.EventType, mandatory: false, properties, Encoding.UTF8.GetBytes(message.PayloadJson), timeout.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Whatever went wrong, the next publication starts from a fresh connection. The type of the failure is enough for the log: the message may carry document data.
            await DropConnectionAsync();
            var failureType = exception.GetType().Name;
            LogPublishFailed(logger, message.EventType, failureType);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open && _connection is { IsOpen: true })
        {
            return open;
        }

        await DropConnectionAsync();
        var factory = new ConnectionFactory
        {
            HostName = _options.Host,
            Port = _options.Port,
            VirtualHost = _options.VirtualHost,
            UserName = _options.UserName,
            Password = _options.Password,
            ClientProvidedName = "securefact",
            AutomaticRecoveryEnabled = false,
            Ssl = new SslOption { Enabled = _options.UseTls, ServerName = _options.Host },
        };

        _connection = await factory.CreateConnectionAsync(cancellationToken);
        _channel = await _connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), cancellationToken);
        await _channel.ExchangeDeclareAsync(_options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        LogConnected(logger, _options.Host, _options.Port, _options.Exchange);
        return _channel;
    }

    private async Task DropConnectionAsync()
    {
        var channel = _channel;
        var connection = _connection;
        _channel = null;
        _connection = null;
        try
        {
            if (channel is not null)
            {
                await channel.DisposeAsync();
            }

            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            // A connection that is already broken may fail to close; it is gone either way.
            var failureType = exception.GetType().Name;
            LogCloseFailed(logger, failureType);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await DropConnectionAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to the message broker at {Host}:{Port}; events go to the exchange {Exchange}.")]
    private static partial void LogConnected(ILogger logger, string host, int port, string exchange);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing {EventType} to the message broker failed ({FailureType}); the outbox will retry.")]
    private static partial void LogPublishFailed(ILogger logger, string eventType, string failureType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Closing the broker connection failed ({FailureType}); it is discarded.")]
    private static partial void LogCloseFailed(ILogger logger, string failureType);
}
