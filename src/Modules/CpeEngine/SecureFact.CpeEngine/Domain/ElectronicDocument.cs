using SecureFact.CpeEngine.Contracts;
using SecureFact.Platform.Persistence;

namespace SecureFact.CpeEngine.Domain;

internal sealed class ElectronicDocument : ITenantOwned
{
    private ElectronicDocument()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid DocumentId { get; private set; }

    public Guid CompanyId { get; private set; }

    /// <summary>Issue date of the document; for a daily summary, the date of the receipts it reports.</summary>
    public DateOnly IssueDate { get; private set; }

    /// <summary>Notes only: the billing document the note modifies and its type (01 invoice or 03 receipt).</summary>
    public Guid? ReferenceDocumentId { get; private set; }

    public string? ReferenceTypeCode { get; private set; }

    public string DocumentTypeCode { get; private set; } = string.Empty;

    public string Series { get; private set; } = string.Empty;

    public long Number { get; private set; }

    public string FileBaseName { get; private set; } = string.Empty;

    public EDocumentState State { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>The signed XML as sent to SUNAT. Immutable (database trigger).</summary>
    public string SignedXml { get; private set; } = string.Empty;

    public string DigestValue { get; private set; } = string.Empty;

    public string? Ticket { get; private set; }

    public byte[]? CdrZip { get; private set; }

    public string? CdrProcessId { get; private set; }

    public int? CdrResponseCode { get; private set; }

    public string? CdrDescription { get; private set; }

    public string CdrObservationsJson { get; private set; } = "[]";

    public string? LastErrorCode { get; private set; }

    public string? LastErrorMessage { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public uint Version { get; private set; }

    public EDocumentSnapshot Snapshot => new(State, Attempts);

    public static ElectronicDocument Create(
        Guid id, Guid tenantId, Guid documentId, Guid companyId, string documentTypeCode, string series, long number,
        string fileBaseName, string signedXml, string digestValue, DateTimeOffset now, DateOnly issueDate,
        Guid? referenceDocumentId = null, string? referenceTypeCode = null) => new()
    {
        Id = id,
        TenantId = tenantId,
        DocumentId = documentId,
        CompanyId = companyId,
        IssueDate = issueDate,
        ReferenceDocumentId = referenceDocumentId,
        ReferenceTypeCode = referenceTypeCode,
        DocumentTypeCode = documentTypeCode,
        Series = series,
        Number = number,
        FileBaseName = fileBaseName,
        SignedXml = signedXml,
        DigestValue = digestValue,
        State = EDocumentState.Pending,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>A daily summary has no billing document: it uses its own id as document id, "RC" as type and its correlative as number.</summary>
    public static ElectronicDocument CreateSummary(
        Guid id, Guid tenantId, Guid companyId, DateOnly referenceDate, int correlative, string fileBaseName, string signedXml, string digestValue, DateTimeOffset now) =>
        Create(id, tenantId, id, companyId, SummaryType, "RC", correlative, fileBaseName, signedXml, digestValue, now, referenceDate);

    public const string SummaryType = "RC";

    public bool IsSummary => DocumentTypeCode == SummaryType;

    public void MoveTo(EDocumentSnapshot snapshot, DateTimeOffset now)
    {
        State = snapshot.State;
        Attempts = snapshot.Attempts;
        UpdatedAt = now;
    }

    public void MarkSent(DateTimeOffset now) => SentAt = now;

    public void ScheduleRetry(DateTimeOffset? at) => NextAttemptAt = at;

    public void RecordError(string? code, string? message)
    {
        LastErrorCode = code;
        LastErrorMessage = message is { Length: > 500 } ? message[..500] : message;
    }

    public void RecordTicket(string ticket) => Ticket = ticket;

    public void RecordCdr(byte[] zip, CdrInfo cdr, string observationsJson, DateTimeOffset now)
    {
        CdrZip = zip;
        CdrProcessId = cdr.ProcessId;
        CdrResponseCode = cdr.ResponseCode;
        CdrDescription = cdr.Description.Length > 1000 ? cdr.Description[..1000] : cdr.Description;
        CdrObservationsJson = observationsJson;
        ProcessedAt = now;
    }

    /// <summary>A receipt reported in an accepted summary shows the summary's outcome; the CDR file itself belongs to the summary.</summary>
    public void RecordSummaryOutcome(CdrInfo cdr, string observationsJson, DateTimeOffset now)
    {
        CdrProcessId = cdr.ProcessId;
        CdrResponseCode = cdr.ResponseCode;
        CdrDescription = cdr.Description.Length > 1000 ? cdr.Description[..1000] : cdr.Description;
        CdrObservationsJson = observationsJson;
        ProcessedAt = now;
    }

    /// <summary>Keeps a CDR that could not be trusted or understood, for investigation. It does not decide the document state.</summary>
    public void KeepRawCdr(byte[] zip) => CdrZip = zip;
}

internal sealed class ElectronicDocumentEvent : ITenantOwned
{
    private ElectronicDocumentEvent()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ElectronicDocumentId { get; private set; }

    public EDocumentState FromState { get; private set; }

    public EDocumentState ToState { get; private set; }

    public EDocumentEvent Event { get; private set; }

    public int Attempt { get; private set; }

    public string? Detail { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public static ElectronicDocumentEvent Create(
        Guid tenantId, Guid electronicDocumentId, EDocumentState from, EDocumentState to, EDocumentEvent @event, int attempt, string? detail, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ElectronicDocumentId = electronicDocumentId,
        FromState = from,
        ToState = to,
        Event = @event,
        Attempt = attempt,
        Detail = detail is { Length: > 500 } ? detail[..500] : detail,
        OccurredAt = now,
    };
}

/// <summary>Links a daily summary to a receipt it reports. A receipt is in at most one active (unreleased) summary.</summary>
internal sealed class SummaryItem : ITenantOwned
{
    private SummaryItem()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid SummaryId { get; private set; }

    public Guid ElectronicDocumentId { get; private set; }

    public int LineNumber { get; private set; }

    /// <summary>Set when the summary was rejected: the receipt can then be reported again.</summary>
    public DateTimeOffset? ReleasedAt { get; private set; }

    public static SummaryItem Create(Guid tenantId, Guid summaryId, Guid electronicDocumentId, int lineNumber) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        SummaryId = summaryId,
        ElectronicDocumentId = electronicDocumentId,
        LineNumber = lineNumber,
    };

    public void Release(DateTimeOffset now) => ReleasedAt ??= now;
}
