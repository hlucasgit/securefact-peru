using System.Collections.Concurrent;
using SecureFact.CpeEngine;
using SecureFact.Gre.Contracts;
using SecureFact.Gre.Infrastructure;

namespace SecureFact.Security.Tests;

/// <summary>
/// Stand-in for SUNAT's GRE platform. It answers like the simulator of the product (the markers in the XML decide the answer) unless a test scripts the next outcome, and it records what
/// the pipeline sends. Nothing here touches the network.
/// </summary>
public sealed class FakeGreChannel : IGreChannel
{
    private readonly SandboxGreChannel _simulator = new(new ZipCpePackager(), TimeProvider.System);
    private readonly ConcurrentQueue<GreSubmitOutcome> _submitScript = new();
    private readonly ConcurrentQueue<GreSubmission> _submissions = new();
    private readonly ConcurrentQueue<string> _queries = new();

    public IReadOnlyCollection<GreSubmission> Submissions => [.. _submissions];

    public IReadOnlyCollection<string> Queries => [.. _queries];

    public void Reset()
    {
        _submitScript.Clear();
        _submissions.Clear();
        _queries.Clear();
    }

    public void EnqueueSubmit(GreSubmitOutcome outcome) => _submitScript.Enqueue(outcome);

    public Task<GreSubmitOutcome> SubmitAsync(GreSubmission submission, CancellationToken cancellationToken)
    {
        _submissions.Enqueue(submission);
        return _submitScript.TryDequeue(out var outcome) ? Task.FromResult(outcome) : _simulator.SubmitAsync(submission, cancellationToken);
    }

    public Task<GreTicketOutcome> QueryTicketAsync(GreChannelCredentials credentials, string ticket, CancellationToken cancellationToken)
    {
        _queries.Enqueue(ticket);
        return _simulator.QueryTicketAsync(credentials, ticket, cancellationToken);
    }
}
