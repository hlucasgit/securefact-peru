using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SecureFact.Platform;
using SecureFact.Platform.Messaging;
using SecureFact.SharedKernel.Messaging;

namespace SecureFact.Security.Tests;

/// <summary>
/// How the outbox processor hands a message to its consumers (ADR-035): the consumers of the event's own type first, then the ones of every type (the bus), the first failure stopping
/// the delivery, and the message completed only when all of them returned.
/// </summary>
public sealed class OutboxFanOutTests
{
    private sealed class FakeSource(params OutboxMessage[] messages) : IOutboxSource
    {
        public List<Guid> Completed { get; } = [];

        public List<(Guid Id, string Error)> Failed { get; } = [];

        public List<TimeSpan> Purges { get; } = [];

        public int Removed { get; set; }

        public string Name => "fake";

        public Task<IReadOnlyList<OutboxMessage>> ClaimAsync(int max, Guid? onlyTenant, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OutboxMessage>>(messages);

        public Task CompleteAsync(Guid messageId, CancellationToken cancellationToken)
        {
            Completed.Add(messageId);
            return Task.CompletedTask;
        }

        public Task FailAsync(Guid messageId, string error, CancellationToken cancellationToken)
        {
            Failed.Add((messageId, error));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DeadOutboxMessage>> ListDeadAsync(int max, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DeadOutboxMessage>>([]);

        public Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<int> PurgeDeliveredAsync(TimeSpan retention, CancellationToken cancellationToken)
        {
            Purges.Add(retention);
            return Task.FromResult(Removed);
        }
    }

    private sealed class Recorder
    {
        public List<string> Calls { get; } = [];
    }

    private sealed class Consumer(string eventType, string name, Recorder recorder, bool fails = false) : IIntegrationEventConsumer
    {
        public string EventType => eventType;

        public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            recorder.Calls.Add(name);
            return fails ? throw new InvalidOperationException($"{name} failed") : Task.CompletedTask;
        }
    }

    private static OutboxMessage Message(string type = "billing.document.issued") => new(Guid.NewGuid(), Guid.NewGuid(), type, "{}", 1, DateTimeOffset.UtcNow);

    private static OutboxProcessor Processor(FakeSource source, params IIntegrationEventConsumer[] consumers)
    {
        var services = new ServiceCollection().AddLogging().AddPlatformDataScope();
        services.AddSingleton<IOutboxSource>(source);
        foreach (var consumer in consumers)
        {
            services.AddSingleton<IIntegrationEventConsumer>(consumer);
        }

        return new OutboxProcessor(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<OutboxProcessor>.Instance);
    }

    [Fact]
    public async Task The_consumers_of_the_events_own_type_run_before_the_bus_and_the_message_is_completed_after_both()
    {
        var recorder = new Recorder();
        var message = Message();
        var source = new FakeSource(message);
        // Registered in the opposite order on purpose: the order of delivery does not depend on the order of registration.
        var processor = Processor(
            source, new Consumer(IIntegrationEventConsumer.AnyEvent, "bus", recorder), new Consumer("billing.document.issued", "cpe", recorder), new Consumer("customers.created", "other", recorder));

        var report = await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["cpe", "bus"], recorder.Calls);
        Assert.Equal([message.Id], source.Completed);
        Assert.Empty(source.Failed);
        Assert.Equal(new OutboxReport(1, 0, 0), report);
    }

    [Fact]
    public async Task Nothing_leaves_for_the_bus_while_the_events_own_consumer_fails()
    {
        var recorder = new Recorder();
        var message = Message();
        var source = new FakeSource(message);
        var processor = Processor(source, new Consumer("billing.document.issued", "cpe", recorder, fails: true), new Consumer(IIntegrationEventConsumer.AnyEvent, "bus", recorder));

        var report = await processor.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["cpe"], recorder.Calls);
        Assert.Empty(source.Completed);
        var failure = Assert.Single(source.Failed);
        Assert.Equal(message.Id, failure.Id);
        Assert.Contains("cpe failed", failure.Error, StringComparison.Ordinal);
        Assert.Equal(new OutboxReport(0, 1, 0), report);
    }

    [Fact]
    public async Task A_failing_bus_leaves_the_message_pending_and_the_retry_runs_the_consumers_again()
    {
        var recorder = new Recorder();
        var message = Message();
        var source = new FakeSource(message);
        var processor = Processor(source, new Consumer("billing.document.issued", "cpe", recorder), new Consumer(IIntegrationEventConsumer.AnyEvent, "bus", recorder, fails: true));

        await processor.RunOnceAsync(CancellationToken.None);
        await processor.RunOnceAsync(CancellationToken.None); // the same message is claimed again after its backoff

        Assert.Equal(["cpe", "bus", "cpe", "bus"], recorder.Calls); // idempotent consumers are what makes this safe
        Assert.Empty(source.Completed);
        Assert.Equal(2, source.Failed.Count);
    }

    [Fact]
    public async Task An_event_that_only_the_bus_cares_about_is_delivered_and_one_nobody_handles_fails_visibly()
    {
        var recorder = new Recorder();
        var published = Message("customers.created");
        var source = new FakeSource(published);
        var withBus = Processor(source, new Consumer(IIntegrationEventConsumer.AnyEvent, "bus", recorder));

        await withBus.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["bus"], recorder.Calls);
        Assert.Equal([published.Id], source.Completed);

        var orphan = Message("billing.unknown");
        var lonely = new FakeSource(orphan);
        var report = await Processor(lonely, new Consumer("billing.document.issued", "cpe", recorder)).RunOnceAsync(CancellationToken.None);

        Assert.Empty(lonely.Completed);
        Assert.Contains("No handler registered for 'billing.unknown'", Assert.Single(lonely.Failed).Error, StringComparison.Ordinal);
        Assert.Equal(1, report.Failed);
    }

    [Fact]
    public async Task The_purge_asks_every_source_to_remove_what_was_delivered_before_the_retention()
    {
        var first = new FakeSource { Removed = 3 };
        var processor = Processor(first);

        var removed = await processor.PurgeAsync(TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(3, removed);
        Assert.Equal([TimeSpan.FromDays(30)], first.Purges);
    }
}
