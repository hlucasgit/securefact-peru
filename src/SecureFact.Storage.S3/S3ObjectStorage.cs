using System.Net;
using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using SecureFact.SharedKernel.Storage;

namespace SecureFact.Storage.S3;

/// <summary>
/// <see cref="IObjectStorage"/> over the S3 API (ADR-005): AWS S3 or any compatible server. Every object carries its SHA-256 as metadata and as the additional checksum of the upload, which
/// the store verifies; reading a file checks the hash again against the one the caller recorded. Keys are written once: the bucket keeps versions (and, in production, Object Lock), so an
/// overwrite would not destroy the previous file, but the platform never attempts one.
/// </summary>
public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private const string HashMetadata = "x-amz-meta-sha256";

    private readonly S3StorageOptions _options;
    private readonly AmazonS3Client _client;

    public S3ObjectStorage(IOptions<S3StorageOptions> options)
    {
        _options = options.Value;
        var config = new AmazonS3Config
        {
            ForcePathStyle = _options.ForcePathStyle,
            Timeout = TimeSpan.FromSeconds(Math.Max(_options.RequestTimeoutSeconds, 1)),
            MaxErrorRetry = 2,
        };
        if (!string.IsNullOrWhiteSpace(_options.ServiceUrl))
        {
            config.ServiceURL = _options.ServiceUrl;
            config.AuthenticationRegion = _options.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(_options.Region);
        }

        _client = !string.IsNullOrEmpty(_options.AccessKey) && !string.IsNullOrEmpty(_options.SecretKey)
            ? new AmazonS3Client(new BasicAWSCredentials(_options.AccessKey, _options.SecretKey), config)
            : new AmazonS3Client(config);
    }

    public async Task<StoredObject> PutAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        var hash = SHA256.HashData(content.Span);
        var hex = Convert.ToHexStringLower(hash);

        if (await HeadAsync(key, cancellationToken) is { } existing)
        {
            return existing.Sha256 == hex ? existing : throw new ObjectAlreadyExistsException(key);
        }

        var request = new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = new MemoryStream(content.ToArray()),
            ContentType = contentType,
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
            ChecksumSHA256 = Convert.ToBase64String(hash),
        };
        request.Metadata["sha256"] = hex;
        switch (_options.ServerSideEncryption)
        {
            case "Aes256":
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
                break;
            case "Kms":
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS;
                request.ServerSideEncryptionKeyManagementServiceKeyId = _options.KmsKeyId;
                break;
        }

        if (_options.ObjectLockMode is "Governance" or "Compliance")
        {
            request.ObjectLockMode = _options.ObjectLockMode == "Compliance" ? ObjectLockMode.Compliance : ObjectLockMode.Governance;
            request.ObjectLockRetainUntilDate = DateTime.UtcNow.AddDays(_options.RetentionDays);
        }

        var response = await _client.PutObjectAsync(request, cancellationToken);
        return new StoredObject(key, NormalizeVersion(response.VersionId), hex, content.Length);
    }

    public async Task<StoredObject?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        try
        {
            var response = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _options.Bucket, Key = key }, cancellationToken);
            var hash = response.Metadata[HashMetadata];
            if (string.IsNullOrEmpty(hash))
            {
                // Not written by the platform (no recorded hash): read it to know what it holds.
                hash = Convert.ToHexStringLower(SHA256.HashData(await ReadAsync(key, cancellationToken) ?? []));
            }

            return new StoredObject(key, NormalizeVersion(response.VersionId), hash, response.ContentLength);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<byte[]?> GetAsync(string key, string expectedSha256, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        var content = await ReadAsync(key, cancellationToken);
        if (content is null)
        {
            return null;
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(content));
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase) ? content : throw new ObjectIntegrityException(key, expectedSha256, actual);
    }

    public async Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan validFor, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        var url = await _client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(validFor),

            // The link follows the scheme of the endpoint: an S3-compatible server of the development environment is plain HTTP, AWS and any server with a certificate are HTTPS.
            Protocol = Uri.TryCreate(_options.ServiceUrl, UriKind.Absolute, out var endpoint) && endpoint.Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS,
        });
        return new Uri(url);
    }

    public void Dispose() => _client.Dispose();

    private async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _options.Bucket, Key = key }, cancellationToken);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>A bucket without versioning answers with no version, or with the literal "null".</summary>
    private static string? NormalizeVersion(string? versionId) => string.IsNullOrEmpty(versionId) || versionId == "null" ? null : versionId;

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 900 || key.StartsWith('/') || key.Contains("..", StringComparison.Ordinal) || key.Any(char.IsControl))
        {
            throw new ArgumentException("The key of an object cannot be empty, absolute, contain '..' or control characters, or exceed 900 characters.", nameof(key));
        }
    }
}
