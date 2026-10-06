namespace SecureFact.SharedKernel.Storage;

/// <summary>What the store knows about a file it holds. <see cref="Sha256"/> is the lower-case hexadecimal SHA-256 of the content, calculated before the upload.</summary>
/// <param name="Key">Key of the object in the bucket.</param>
/// <param name="VersionId">Version the store assigned (null when the bucket does not keep versions).</param>
/// <param name="Sha256">SHA-256 of the content, lower-case hexadecimal.</param>
/// <param name="Size">Size in bytes.</param>
public sealed record StoredObject(string Key, string? VersionId, string Sha256, long Size);

/// <summary>A different content was offered for a key that already holds a file: stored documents are written once and never replaced.</summary>
public sealed class ObjectAlreadyExistsException(string key) : InvalidOperationException($"The object '{key}' already exists with a different content.")
{
    public string Key { get; } = key;
}

/// <summary>The content read back does not match the hash recorded when it was stored: the file was altered or damaged.</summary>
public sealed class ObjectIntegrityException(string key, string expectedSha256, string actualSha256)
    : InvalidOperationException($"The object '{key}' does not match its recorded SHA-256.")
{
    public string Key { get; } = key;

    public string ExpectedSha256 { get; } = expectedSha256;

    public string ActualSha256 { get; } = actualSha256;
}

/// <summary>
/// Object storage for the documents the platform must keep (ADR-005): signed XML and CDR. The files are small (kilobytes), so they travel as byte arrays. A key is written once:
/// putting the same content again is a no-op that returns what is stored, putting a different one fails. The hash is calculated before the upload and checked when the file is read.
/// </summary>
public interface IObjectStorage
{
    /// <summary>Stores <paramref name="content"/> under <paramref name="key"/>, or returns what is already stored when the content is the same. Throws <see cref="ObjectAlreadyExistsException"/> when the key holds a different content.</summary>
    Task<StoredObject> PutAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken);

    /// <summary>What is stored under the key, or null when nothing is. It does not read the content.</summary>
    Task<StoredObject?> HeadAsync(string key, CancellationToken cancellationToken);

    /// <summary>The content stored under the key, checked against <paramref name="expectedSha256"/>; null when nothing is stored. Throws <see cref="ObjectIntegrityException"/> when the content does not match.</summary>
    Task<byte[]?> GetAsync(string key, string expectedSha256, CancellationToken cancellationToken);

    /// <summary>A link that downloads the file without credentials for <paramref name="validFor"/>. The caller authorises the download before handing it out.</summary>
    Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan validFor, CancellationToken cancellationToken);
}
