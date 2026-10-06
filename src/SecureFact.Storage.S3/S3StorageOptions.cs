namespace SecureFact.Storage.S3;

/// <summary>
/// Connection to an S3-compatible store (section <c>Storage:S3</c>). The keys come from the environment or a secret store, never from a file in the repository, and are never logged;
/// when they are empty the SDK's own credential chain applies (an IAM role in AWS).
/// </summary>
public sealed class S3StorageOptions
{
    public const string SectionName = "Storage:S3";

    /// <summary>Endpoint of an S3-compatible server (SeaweedFS, MinIO...). Empty for AWS S3, which the region selects.</summary>
    public string ServiceUrl { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";

    /// <summary>Bucket that holds the documents. Required. It is created and configured outside the application (versioning on, Object Lock in production).</summary>
    public string Bucket { get; set; } = string.Empty;

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Path-style addressing (<c>host/bucket/key</c>), which the S3-compatible servers expect.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Server-side encryption of every object: <c>None</c>, <c>Aes256</c> (keys managed by the store) or <c>Kms</c> (with <see cref="KmsKeyId"/>).</summary>
    public string ServerSideEncryption { get; set; } = "None";

    public string KmsKeyId { get; set; } = string.Empty;

    /// <summary>Retention that each new object is locked with: <c>None</c>, <c>Governance</c> or <c>Compliance</c>. The bucket must have Object Lock enabled for the others.</summary>
    public string ObjectLockMode { get; set; } = "None";

    /// <summary>Days an object stays locked from the moment it is stored; required when <see cref="ObjectLockMode"/> is not <c>None</c>. The legal retention period is the issuer's to confirm.</summary>
    public int RetentionDays { get; set; }

    public int RequestTimeoutSeconds { get; set; } = 30;
}
