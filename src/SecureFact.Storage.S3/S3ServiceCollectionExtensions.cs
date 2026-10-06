using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SecureFact.SharedKernel.Storage;

namespace SecureFact.Storage.S3;

public static class S3ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IObjectStorage"/> over S3 with the settings of <paramref name="section"/> (<c>Storage:S3</c>). The settings are checked when the host starts.
    /// </summary>
    public static IServiceCollection AddS3ObjectStorage(this IServiceCollection services, IConfiguration section)
    {
        services.AddOptions<S3StorageOptions>()
            .Bind(section)
            .Validate(o => !string.IsNullOrWhiteSpace(o.Bucket), "Storage:S3:Bucket is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Region), "Storage:S3:Region is required.")
            .Validate(o => string.IsNullOrWhiteSpace(o.ServiceUrl) || Uri.TryCreate(o.ServiceUrl, UriKind.Absolute, out _), "Storage:S3:ServiceUrl must be an absolute URL.")
            .Validate(o => string.IsNullOrEmpty(o.AccessKey) == string.IsNullOrEmpty(o.SecretKey), "Storage:S3:AccessKey and Storage:S3:SecretKey go together (or neither, to use the credential chain of the SDK).")
            .Validate(o => o.ServerSideEncryption is "None" or "Aes256" or "Kms", "Storage:S3:ServerSideEncryption must be None, Aes256 or Kms.")
            .Validate(o => o.ServerSideEncryption != "Kms" || !string.IsNullOrWhiteSpace(o.KmsKeyId), "Storage:S3:KmsKeyId is required with Kms encryption.")
            .Validate(o => o.ObjectLockMode is "None" or "Governance" or "Compliance", "Storage:S3:ObjectLockMode must be None, Governance or Compliance.")
            .Validate(o => o.ObjectLockMode == "None" || o.RetentionDays > 0, "Storage:S3:RetentionDays is required (greater than zero) with Object Lock.")
            .ValidateOnStart();
        services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        return services;
    }
}
