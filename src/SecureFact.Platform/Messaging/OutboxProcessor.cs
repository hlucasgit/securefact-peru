using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Messaging;

namespace SecureFact.Platform.Messaging;

public static class OutboxServiceCollectionExtensions
{
    /// <summary>Registers the dispatcher. Modules add their <see cref="IOutboxSource"/> and handlers.</summary>
    public static IServiceCollection AddOutboxProcessing(this IServiceCollection services) =>
        services.AddSingleton<IOutboxProcessor, OutboxProcessor>();

    /// <summary>
    /// Publishes every outbox message to the message bus once its own handler has run (ADR-035). Needs an <see cref="IMessageBus"/>: without one the platform delivers
    /// only to its own consumers.
    /// </summary>
    public static IServiceCollection AddBusPublishing(this IServiceCollection services) =>
        services.AddScoped<IIntegrationEventConsumer, BusPublishingConsumer>();
}

/// <summary>Consumer of every event type that hands the message to the bus. The message id travels with it, so subscribers can drop the duplicates of an at-least-once delivery.</summary>
internal sealed class BusPublishingConsumer(IMessageBus bus) : IIntegrationEventConsumer
{
    public string EventType => IIntegrationEventConsumer.AnyEvent;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken) =>
        bus.PublishAsync(new BusMessage(message.Id, message.TenantId, message.EventType, message.PayloadJson, message.CreatedAt), cancellationToken);
}

/// <summary>
/// Delivers outbox messages of every registered <see cref="IOutboxSource"/> to the matching <see cref="IIntegrationEventConsumer"/>.
/// At-least-once: a message is completed only after its handler returned, so a crash in between delivers it again.
/// </summary>
public sealed partial class OutboxProcessor(IServiceScopeFactory scopes, ILogger<OutboxProcessor> logger) : IOutboxProcessor
{
    private const int BatchSize = 50;
    private const int MaxErrorLength = 500;

    public async Task<OutboxReport> RunOnceAsync(CancellationToken cancellationToken, Guid? onlyTenant = null)
    {
        string[] sources;
        await using (var probe = scopes.CreateAsyncScope())
        {
            sources = probe.ServiceProvider.GetServices<IOutboxSource>().Select(s => s.Name).ToArray();
        }

        var report = OutboxReport.Empty;
        foreach (var source in sources)
        {
            IReadOnlyList<OutboxMessage> claimed;
            await using (var scope = scopes.CreateAsyncScope())
            {
                scope.ServiceProvider.GetRequiredService<DataScope>().UsePlatform("outbox: claim messages");
                claimed = await Source(scope.ServiceProvider, source).ClaimAsync(BatchSize, onlyTenant, cancellationToken);
            }

            foreach (var message in claimed)
            {
                report = await DeliverAsync(source, message, report, cancellationToken);
            }
        }

        return report;
    }

    private async Task<OutboxReport> DeliverAsync(string source, OutboxMessage message, OutboxReport report, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UseTenant(new TenantId(message.TenantId));
        var outbox = Source(scope.ServiceProvider, source);

        string? error = null;
        try
        {
            // The consumers of the event's own type run first and the ones of every type (the bus) after them, so nothing leaves the platform before its own effect happened.
            // The first failure stops the delivery; the retry runs the consumers again, which is why they are idempotent.
            var consumers = scope.ServiceProvider.GetServices<IIntegrationEventConsumer>()
                .Where(h => h.EventType == message.EventType || h.EventType == IIntegrationEventConsumer.AnyEvent)
                .OrderBy(h => h.EventType == IIntegrationEventConsumer.AnyEvent ? 1 : 0)
                .ToList();
            if (consumers.Count == 0)
            {
                error = $"No handler registered for '{message.EventType}'.";
            }

            foreach (var consumer in consumers)
            {
                await consumer.HandleAsync(message, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The type of the failure is enough for operators; the message may carry document data and stays out of the log.
            LogHandlerFailed(logger, message.EventType, exception.GetType().Name);
            error = $"{exception.GetType().Name}: {exception.Message}";
        }

        if (error is null)
        {
            await outbox.CompleteAsync(message.Id, CancellationToken.None);
            return report with { Delivered = report.Delivered + 1 };
        }

        await outbox.FailAsync(message.Id, error.Length > MaxErrorLength ? error[..MaxErrorLength] : error, CancellationToken.None);
        var dead = message.Attempts >= OutboxPolicy.MaxAttempts;
        if (dead)
        {
            LogDead(logger, message.EventType, message.Id);
        }

        return report with { Failed = report.Failed + 1, Dead = report.Dead + (dead ? 1 : 0) };
    }

    public async Task<int> PurgeAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        string[] sources;
        await using (var probe = scopes.CreateAsyncScope())
        {
            sources = probe.ServiceProvider.GetServices<IOutboxSource>().Select(s => s.Name).ToArray();
        }

        var removed = 0;
        foreach (var source in sources)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<DataScope>().UsePlatform("outbox: purge delivered messages");
            removed += await Source(scope.ServiceProvider, source).PurgeDeliveredAsync(retention, cancellationToken);
        }

        return removed;
    }

    private static IOutboxSource Source(IServiceProvider services, string name) =>
        services.GetServices<IOutboxSource>().Single(s => s.Name == name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox handler for {EventType} failed ({FailureType}); it will be retried.")]
    private static partial void LogHandlerFailed(ILogger logger, string eventType, string failureType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} of type {EventType} is dead after the maximum number of attempts and needs an operator.")]
    private static partial void LogDead(ILogger logger, string eventType, Guid messageId);
}
