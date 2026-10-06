using SecureFact.SharedKernel.Results;

namespace SecureFact.CpeEngine.Contracts;

/// <summary>Integration event types published by the CPE engine through its transactional outbox (ADR-036). The payload is <see cref="ElectronicDocumentEventPayload"/> (JSON, web casing).</summary>
public static class CpeEvents
{
    /// <summary>The XML of an electronic document was generated, signed and stored.</summary>
    public const string DocumentPrepared = "cpe.document.prepared";

    /// <summary>SUNAT answered with a CDR (accepted, accepted with observations or rejected) and the engine recorded it.</summary>
    public const string DocumentAnswered = "cpe.document.answered";
}

/// <summary>What an event of <see cref="CpeEvents"/> says: which electronic document and which billing document it concerns. It carries no document data.</summary>
public sealed record ElectronicDocumentEventPayload(Guid ElectronicDocumentId, Guid DocumentId, Guid CompanyId, string DocumentTypeCode, string Series, long Number);

/// <summary>The kinds of file the platform keeps in object storage for an electronic document (ADR-005, ADR-036).</summary>
public static class ArchiveKinds
{
    /// <summary>The signed XML exactly as it was sent to SUNAT.</summary>
    public const string SignedXml = "signed-xml";

    /// <summary>The CDR as SUNAT returned it (a ZIP).</summary>
    public const string CdrZip = "cdr-zip";
}

/// <summary>A file kept in object storage for an electronic document, with a short-lived link to download it.</summary>
/// <param name="Kind">One of <see cref="ArchiveKinds"/>.</param>
/// <param name="Sha256">SHA-256 of the content, lower-case hexadecimal, recorded before the upload.</param>
/// <param name="DownloadUrl">Link that downloads the file without credentials until <paramref name="DownloadExpiresAt"/>.</param>
public sealed record ArchivedFileDto(string Kind, string ContentType, long SizeBytes, string Sha256, DateTimeOffset StoredAt, Uri DownloadUrl, DateTimeOffset DownloadExpiresAt);

public interface IDocumentArchive
{
    /// <summary>The files kept for an electronic document (none while the archive has not run yet), each with a link valid for <see cref="LinkLifetime"/>.</summary>
    Task<Result<IReadOnlyList<ArchivedFileDto>>> ListAsync(Guid electronicDocumentId, CancellationToken cancellationToken);

    /// <summary>How long a download link of <see cref="ListAsync"/> stays valid.</summary>
    static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(5);
}
