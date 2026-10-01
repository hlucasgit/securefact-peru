using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine.Contracts;

/// <summary>Where an electronic document is in its life with SUNAT.</summary>
public enum EDocumentState
{
    /// <summary>XML generated, not signed yet.</summary>
    Pending,

    /// <summary>Signed and packaged, ready to send (or to send again after a transient failure).</summary>
    ReadyToSend,

    /// <summary>A request to SUNAT is in flight. If the worker dies here the outcome is unknown and must be looked up, not assumed.</summary>
    Sending,

    /// <summary>SUNAT returned a ticket; the CDR is fetched with <c>getStatus</c>.</summary>
    AwaitingTicket,

    /// <summary>Terminal. Accepted CDR: the document is immutable.</summary>
    Accepted,

    /// <summary>Terminal. Accepted with observations: the document is immutable.</summary>
    AcceptedWithObservations,

    /// <summary>Terminal. Rejected by SUNAT: the document is not a valid CPE and is never edited; a new one is issued.</summary>
    Rejected,

    /// <summary>Sending stopped (permanent fault or retries exhausted). Needs a person; only <see cref="EDocumentEvent.ManualRetry"/> leaves it.</summary>
    Failed,
}

public enum EDocumentEvent
{
    DocumentSigned,
    SendStarted,
    CdrAccepted,
    CdrAcceptedWithObservations,
    CdrRejected,
    TicketIssued,

    /// <summary>The ticket is still in process (<c>getStatus</c> 98) or the status query failed transiently: stay waiting.</summary>
    StillProcessing,

    /// <summary>Network, timeout, 5xx or SUNAT exception: safe to try again later.</summary>
    TransientFailure,

    /// <summary>Fault that retrying the same package will not fix.</summary>
    PermanentFailure,

    /// <summary>An operator re-queues a failed document.</summary>
    ManualRetry,
}

/// <param name="State">State after the event.</param>
/// <param name="Attempts">Number of times sending started so far.</param>
public sealed record EDocumentSnapshot(EDocumentState State, int Attempts)
{
    public static EDocumentSnapshot New { get; } = new(EDocumentState.Pending, 0);

    public bool IsTerminal => State is EDocumentState.Accepted or EDocumentState.AcceptedWithObservations or EDocumentState.Rejected;
}

/// <summary>Pure transition function of the electronic document lifecycle. Impossible transitions are errors, never silent.</summary>
public interface IEDocumentStateMachine
{
    int MaxAttempts { get; }

    Result<EDocumentSnapshot> Apply(EDocumentSnapshot current, EDocumentEvent @event);
}
