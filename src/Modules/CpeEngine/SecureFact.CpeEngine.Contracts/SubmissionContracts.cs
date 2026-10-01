namespace SecureFact.CpeEngine.Contracts;

/// <summary>
/// SOL credentials used to authenticate against SUNAT (WS-Security UsernameToken; the user name is RUC + SOL user,
/// Programmer Manual §2.2). <see cref="ToString"/> never prints the password.
/// </summary>
public sealed record SunatCredentials(string Ruc, string SolUser, string SolPassword)
{
    public string UserName => Ruc + SolUser;

    public override string ToString() => $"SunatCredentials {{ Ruc = {Ruc}, SolUser = {SolUser}, SolPassword = ***** }}";
}

public enum SunatSide
{
    Unknown,

    /// <summary>SOAP <c>Server</c> fault code: the problem is probably on SUNAT's side.</summary>
    Server,

    /// <summary>SOAP <c>Client</c> fault code: the problem is probably in what the sender submitted.</summary>
    Client,
}

/// <param name="Side">Where the fault code says the problem is.</param>
/// <param name="Code">SUNAT numeric code carried by the fault (<c>soap-env:Server.0835</c> → 835), when present.</param>
/// <param name="Message">Fault text, truncated and free of credentials.</param>
/// <param name="Retryable">True when sending the same package again may succeed (server side, SUNAT exception range or transport failure).</param>
public sealed record SunatFault(SunatSide Side, int? Code, string Message, bool Retryable)
{
    public SunatCodeKind Kind => Code is { } code ? SunatCodes.Classify(code) : SunatCodeKind.Unclassified;
}

public enum ChannelOutcome
{
    /// <summary>SUNAT returned a ZIP holding the CDR (accepted or rejected: read it with <see cref="ICdrParser"/>).</summary>
    CdrReceived,

    /// <summary>SUNAT accepted the package for asynchronous processing and returned a ticket.</summary>
    TicketIssued,

    /// <summary>The ticket is still being processed (<c>statusCode</c> 98).</summary>
    InProgress,

    /// <summary>SUNAT answered with a SOAP fault or an HTTP error.</summary>
    Fault,

    /// <summary>No usable answer: network error, timeout or oversized/garbled response. Always retryable.</summary>
    Unreachable,
}

public sealed record ChannelReply(ChannelOutcome Outcome, byte[]? CdrZip = null, string? Ticket = null, SunatFault? Fault = null)
{
    public static ChannelReply Cdr(byte[] zip) => new(ChannelOutcome.CdrReceived, CdrZip: zip);

    public static ChannelReply Issued(string ticket) => new(ChannelOutcome.TicketIssued, Ticket: ticket);

    public static ChannelReply Processing() => new(ChannelOutcome.InProgress);

    public static ChannelReply Failed(SunatFault fault) => new(ChannelOutcome.Fault, Fault: fault);

    public static ChannelReply Down(string message) =>
        new(ChannelOutcome.Unreachable, Fault: new SunatFault(SunatSide.Unknown, null, message, Retryable: true));
}

/// <summary>
/// Transport to SUNAT's <c>billService</c> (SOAP). It only moves packages: it does not sign, build or interpret documents.
/// Implementations never throw for remote failures; they return <see cref="ChannelReply"/>.
/// </summary>
public interface ICpeSubmissionChannel
{
    /// <summary><c>sendBill</c>: one signed document per ZIP, answered synchronously with the CDR ZIP.</summary>
    Task<ChannelReply> SendBillAsync(SunatCredentials credentials, string zipFileName, byte[] zip, CancellationToken cancellationToken = default);

    /// <summary><c>sendSummary</c>: daily summaries and voids; answered with a ticket.</summary>
    Task<ChannelReply> SendSummaryAsync(SunatCredentials credentials, string zipFileName, byte[] zip, CancellationToken cancellationToken = default);

    /// <summary><c>getStatus</c>: result of a ticket (<c>0</c> processed, <c>98</c> in progress, <c>99</c> processed with errors).</summary>
    Task<ChannelReply> GetStatusAsync(SunatCredentials credentials, string ticket, CancellationToken cancellationToken = default);
}

/// <summary>Where the channel connects. Endpoints come from the Programmer Manual (§2.2–2.3, services available in production and beta).</summary>
public sealed record SunatChannelOptions(Uri Endpoint, TimeSpan Timeout)
{
    /// <summary>Production invoice service. Real taxpayers only.</summary>
    public static SunatChannelOptions Production { get; } = new(new Uri("https://e-factura.sunat.gob.pe/ol-ti-itcpfegem/billService"), TimeSpan.FromSeconds(60));

    /// <summary>SUNAT's beta service: for functional tests only, never for load or stress testing.</summary>
    public static SunatChannelOptions Beta { get; } = new(new Uri("https://e-beta.sunat.gob.pe/ol-ti-itcpfegem-beta/billService"), TimeSpan.FromSeconds(60));
}
