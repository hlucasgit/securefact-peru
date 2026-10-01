using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine.Contracts;

/// <summary>Outcome of SUNAT's processing as stated by the CDR response code (Programmer Manual, Annex 1).</summary>
public enum CdrStatus
{
    /// <summary>Response code 0 and no notes.</summary>
    Accepted,

    /// <summary>Response code 0 with one or more notes (observations, codes 4000+).</summary>
    AcceptedWithObservations,

    /// <summary>Any non-zero response code. The document is not a valid CPE.</summary>
    Rejected,
}

/// <summary>Family of a SUNAT message code, by the ranges of the Programmer Manual (Annex 1).</summary>
public enum SunatCodeKind
{
    /// <summary>0100 – 0999: exception raised by SUNAT while processing; the sender may retry.</summary>
    SunatException,

    /// <summary>1000 – 1999: the taxpayer's submission is malformed or unauthorised; nothing was registered.</summary>
    TaxpayerException,

    /// <summary>2000 – 3999: the document was received and rejected.</summary>
    Rejection,

    /// <summary>4000 and above: the document was accepted with an observation.</summary>
    Observation,

    /// <summary>Zero, or a value outside the documented ranges.</summary>
    Unclassified,
}

public static class SunatCodes
{
    public static SunatCodeKind Classify(int code) => code switch
    {
        >= 100 and <= 999 => SunatCodeKind.SunatException,
        >= 1000 and <= 1999 => SunatCodeKind.TaxpayerException,
        >= 2000 and <= 3999 => SunatCodeKind.Rejection,
        >= 4000 => SunatCodeKind.Observation,
        _ => SunatCodeKind.Unclassified,
    };

    /// <summary>True when sending the same document again can change the outcome without changing the document.</summary>
    public static bool IsRetryable(int code) => Classify(code) == SunatCodeKind.SunatException;
}

/// <param name="Code">Four-digit code from a <c>cbc:Note</c>, e.g. <c>4031</c>; empty when the note carries no code.</param>
/// <param name="Message">Text that follows the code.</param>
public sealed record CdrObservation(string Code, string Message);

/// <param name="ProcessId">Reception process number (<c>cbc:ID</c>).</param>
/// <param name="ReceivedDate">Date SUNAT received the document (<c>cbc:IssueDate</c>).</param>
/// <param name="ReceivedTime">Time SUNAT received the document (<c>cbc:IssueTime</c>).</param>
/// <param name="ResponseDate">Date the CDR was generated.</param>
/// <param name="ResponseTime">Time the CDR was generated.</param>
/// <param name="SunatRuc">RUC of the issuer of the CDR (SUNAT).</param>
/// <param name="TaxpayerRuc">RUC of the taxpayer that sent the document.</param>
/// <param name="ReferenceId">Identifier of the processed document, as <c>SERIE-NUMERO</c>.</param>
/// <param name="ResponseCode">SUNAT response code; zero means accepted.</param>
/// <param name="Description">Response description.</param>
/// <param name="Observations">Notes attached to the response.</param>
public sealed record CdrInfo(
    string ProcessId,
    DateOnly ReceivedDate,
    TimeOnly ReceivedTime,
    DateOnly ResponseDate,
    TimeOnly ResponseTime,
    string SunatRuc,
    string TaxpayerRuc,
    string ReferenceId,
    int ResponseCode,
    string Description,
    IReadOnlyList<CdrObservation> Observations)
{
    public CdrStatus Status => ResponseCode != 0
        ? CdrStatus.Rejected
        : Observations.Count > 0 ? CdrStatus.AcceptedWithObservations : CdrStatus.Accepted;
}

/// <summary>
/// Reads the CDR (<c>ApplicationResponse</c> UBL) that SUNAT returns. Parsing is strict about structure and
/// conservative about meaning: only a response code of exactly 0 is "accepted"; anything else is "rejected".
/// </summary>
public interface ICdrParser
{
    /// <summary>Parses the CDR XML. The text is never resolved against external entities or DTDs.</summary>
    Result<CdrInfo> Parse(string cdrXml);

    /// <summary>Unzips (<c>R-{name}.zip</c> holding <c>R-{name}.xml</c>) and parses the CDR.</summary>
    Result<CdrInfo> ParseZip(byte[] cdrZip);
}
