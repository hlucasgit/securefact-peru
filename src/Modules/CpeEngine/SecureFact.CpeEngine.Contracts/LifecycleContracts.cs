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

/// <summary>Electronic side of a numbered billing document: its signed XML, its state with SUNAT and the CDR.</summary>
/// <param name="CdrResponseCode">SUNAT response code from the CDR; null until a CDR arrives.</param>
/// <param name="LastErrorCode">Last transport or SUNAT fault code (never a secret), for support.</param>
public sealed record ElectronicDocumentDto(
    Guid Id,
    Guid TenantId,
    Guid DocumentId,
    Guid CompanyId,
    string DocumentTypeCode,
    string Series,
    long Number,
    string FileBaseName,
    EDocumentState State,
    int Attempts,
    string DigestValue,
    string? Ticket,
    string? CdrProcessId,
    int? CdrResponseCode,
    string? CdrDescription,
    IReadOnlyList<CdrObservation> CdrObservations,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? ProcessedAt);

public sealed record ElectronicDocumentEventDto(Guid Id, EDocumentState From, EDocumentState To, EDocumentEvent Event, int Attempt, string? Detail, DateTimeOffset OccurredAt);

public interface IElectronicDocumentService
{
    /// <summary>
    /// Builds the UBL XML of a numbered document, signs it with the company's active certificate and stores the result as the
    /// electronic document (state <see cref="EDocumentState.ReadyToSend"/>). Idempotent: a document is prepared once.
    /// </summary>
    Task<Result<ElectronicDocumentDto>> PrepareAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    /// Sends the signed invoice to SUNAT (<c>sendBill</c>) and records the CDR. Idempotent for documents with a final answer.
    /// Receipts (03) are reported in daily summaries and are not sent by this method.
    /// </summary>
    Task<Result<ElectronicDocumentDto>> SendAsync(Guid electronicDocumentId, CancellationToken cancellationToken);

    /// <summary>An operator puts a failed document back in the queue (resets its send attempts).</summary>
    Task<Result<ElectronicDocumentDto>> RetryAsync(Guid electronicDocumentId, CancellationToken cancellationToken);

    Task<Result<ElectronicDocumentDto>> GetAsync(Guid electronicDocumentId, CancellationToken cancellationToken);

    Task<Result<ElectronicDocumentDto>> GetByDocumentAsync(Guid documentId, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<ElectronicDocumentEventDto>>> ListEventsAsync(Guid electronicDocumentId, CancellationToken cancellationToken);

    /// <summary>The signed XML exactly as it was sent. Never changes after preparation.</summary>
    Task<Result<string>> GetSignedXmlAsync(Guid electronicDocumentId, CancellationToken cancellationToken);

    /// <summary>The CDR ZIP exactly as SUNAT returned it; not found until a CDR arrives.</summary>
    Task<Result<byte[]>> GetCdrZipAsync(Guid electronicDocumentId, CancellationToken cancellationToken);
}
