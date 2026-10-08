using SecureFact.Gre.Contracts;
using SecureFact.Platform.Persistence;

namespace SecureFact.Gre.Domain;

/// <summary>
/// A guide of the sender. Once prepared its number, its data and its signed XML never change (database trigger); once SUNAT answers with a CDR the whole row is final (R-010, rule 8 of the
/// project: accepted documents and their CDR are immutable).
/// </summary>
internal sealed class Guide : ITenantOwned
{
    /// <summary>After this many calls to SUNAT without an answer the guide fails for good and a new one must be issued.</summary>
    public const int MaxAttempts = 12;

    public static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1)];

    private Guide()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid CompanyId { get; private set; }

    public Guid SeriesId { get; private set; }

    public string Series { get; private set; } = string.Empty;

    public long Number { get; private set; }

    public DateOnly IssueDate { get; private set; }

    public string MotiveCode { get; private set; } = string.Empty;

    public string ModalityCode { get; private set; } = string.Empty;

    public string RecipientDocument { get; private set; } = string.Empty;

    public string RecipientName { get; private set; } = string.Empty;

    /// <summary>What was asked, as JSON, for the screen and the printed copy. The XML is the legal document.</summary>
    public string RequestJson { get; private set; } = "{}";

    public string FileBaseName { get; private set; } = string.Empty;

    public string SignedXml { get; private set; } = string.Empty;

    public string DigestValue { get; private set; } = string.Empty;

    public GreState State { get; private set; }

    public string? Ticket { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    public byte[]? CdrZip { get; private set; }

    public string? CdrProcessId { get; private set; }

    public int? CdrResponseCode { get; private set; }

    public string? CdrDescription { get; private set; }

    public string CdrObservationsJson { get; private set; } = "[]";

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public uint Version { get; private set; }

    public bool IsFinal => State is GreState.Accepted or GreState.AcceptedWithObservations or GreState.Rejected or GreState.Failed;

    public static Guide Prepare(
        Guid id, Guid tenantId, Guid companyId, GreSeries series, long number, DateOnly issueDate, string motiveCode, string modalityCode,
        string recipientDocument, string recipientName, string requestJson, string fileBaseName, string signedXml, string digestValue, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        CompanyId = companyId,
        SeriesId = series.Id,
        Series = series.Code,
        Number = number,
        IssueDate = issueDate,
        MotiveCode = motiveCode,
        ModalityCode = modalityCode,
        RecipientDocument = recipientDocument,
        RecipientName = recipientName,
        RequestJson = requestJson,
        FileBaseName = fileBaseName,
        SignedXml = signedXml,
        DigestValue = digestValue,
        State = GreState.Prepared,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>SUNAT took the file and gave a ticket.</summary>
    public void MarkPending(string ticket, DateTimeOffset now)
    {
        Ticket = ticket;
        State = GreState.Pending;
        Attempts++;
        SentAt ??= now;
        NextAttemptAt = now + Backoff[0];
        ErrorCode = null;
        ErrorMessage = null;
        UpdatedAt = now;
    }

    /// <summary>SUNAT or the network could not answer: try the same call again later, or fail for good once the attempts run out.</summary>
    public void RecordTransient(string? code, string? message, DateTimeOffset now)
    {
        Attempts++;
        ErrorCode = code;
        ErrorMessage = Clip(message, 500);
        UpdatedAt = now;
        if (Attempts >= MaxAttempts)
        {
            Fail(code, message, now);
            return;
        }

        NextAttemptAt = now + Backoff[Math.Min(Attempts, Backoff.Length) - 1];
    }

    /// <summary>The submission was refused for good, or the attempts ran out. There is no CDR: the number is consumed and a new guide must be issued.</summary>
    public void Fail(string? code, string? message, DateTimeOffset now)
    {
        State = GreState.Failed;
        ErrorCode = code;
        ErrorMessage = Clip(message, 500);
        NextAttemptAt = null;
        ProcessedAt = now;
        UpdatedAt = now;
    }

    /// <summary>SUNAT answered with a CDR: the state is the one of its response code.</summary>
    public void ApplyCdr(byte[] zip, string processId, int responseCode, string description, string observationsJson, bool hasObservations, DateTimeOffset now)
    {
        CdrZip = zip;
        CdrProcessId = processId;
        CdrResponseCode = responseCode;
        CdrDescription = Clip(description, 1000);
        CdrObservationsJson = observationsJson;
        State = responseCode != 0 ? GreState.Rejected : hasObservations ? GreState.AcceptedWithObservations : GreState.Accepted;
        ErrorCode = null;
        ErrorMessage = null;
        NextAttemptAt = null;
        ProcessedAt = now;
        UpdatedAt = now;
    }

    /// <summary>The answer is not there yet: ask again later.</summary>
    public void ScheduleNextCheck(DateTimeOffset now)
    {
        Attempts++;
        UpdatedAt = now;
        if (Attempts >= MaxAttempts)
        {
            Fail("SF-GRE-TIMEOUT", "SUNAT no contestó el ticket tras varios intentos.", now);
            return;
        }

        NextAttemptAt = now + Backoff[Math.Min(Attempts, Backoff.Length) - 1];
    }

    private static string? Clip(string? value, int max) => value is null ? null : value.Length > max ? value[..max] : value;
}
