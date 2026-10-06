using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SecureFact.Platform.Storage;
using SecureFact.SharedKernel.Storage;
using SecureFact.Storage.S3;

namespace SecureFact.Security.Tests;

/// <summary>One real S3-compatible server per test run: the SeaweedFS of the development environment (ADR-005), with credentials that exist only in the container.</summary>
public sealed class S3Fixture : IAsyncLifetime
{
    public const string AccessKey = "securefact-test-access";
    public const string SecretKey = "securefact-test-secret-key";

    private readonly IContainer _container = new ContainerBuilder("chrislusf/seaweedfs:4.48")
        .WithCommand("server", "-dir=/data", "-s3", "-s3.port=8333", "-ip.bind=0.0.0.0")
        .WithEnvironment("AWS_ACCESS_KEY_ID", AccessKey)
        .WithEnvironment("AWS_SECRET_ACCESS_KEY", SecretKey)
        .WithPortBinding(8333, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(8333))
        .Build();

    public string ServiceUrl => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8333)}";

    public async Task InitializeAsync() => await _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public S3StorageOptions Options(string bucket) => new()
    {
        ServiceUrl = ServiceUrl,
        Bucket = bucket,
        AccessKey = AccessKey,
        SecretKey = SecretKey,
        RequestTimeoutSeconds = 30,
    };

    public AmazonS3Client RawClient() =>
        new(new Amazon.Runtime.BasicAWSCredentials(AccessKey, SecretKey), new AmazonS3Config { ServiceURL = ServiceUrl, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });

    /// <summary>A new bucket with versioning on, as <c>s3-init</c> creates it.</summary>
    public async Task<string> NewBucketAsync()
    {
        var bucket = $"test-{Guid.NewGuid():N}";
        using var client = RawClient();
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        await client.PutBucketVersioningAsync(new PutBucketVersioningRequest { BucketName = bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });
        return bucket;
    }
}

[Collection(ApiTestGroup.Name)]
public sealed class ObjectStorageTests(S3Fixture s3)
{
    private static readonly byte[] Xml = Encoding.UTF8.GetBytes("<Invoice>firmada</Invoice>");

    private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private async Task<(S3ObjectStorage Storage, string Bucket)> NewStorageAsync()
    {
        var bucket = await s3.NewBucketAsync();
        return (new S3ObjectStorage(Microsoft.Extensions.Options.Options.Create(s3.Options(bucket))), bucket);
    }

    [Fact]
    public async Task A_file_is_stored_with_its_hash_and_read_back_verified()
    {
        var (storage, _) = await NewStorageAsync();
        using var _ = storage;
        const string key = "t/abc/c/def/2026/01/F001-1/signed-xml/v1";

        var stored = await storage.PutAsync(key, Xml, "application/xml", CancellationToken.None);

        Assert.Equal(key, stored.Key);
        Assert.Equal(Sha(Xml), stored.Sha256);
        Assert.Equal(Xml.Length, stored.Size);
        Assert.False(string.IsNullOrEmpty(stored.VersionId)); // the bucket keeps versions
        var head = await storage.HeadAsync(key, CancellationToken.None);
        Assert.Equal(stored.Sha256, head!.Sha256);
        Assert.Equal(Xml.Length, head.Size);
        Assert.Equal(Xml, await storage.GetAsync(key, stored.Sha256, CancellationToken.None));
    }

    [Fact]
    public async Task A_key_is_written_once_the_same_content_is_a_no_op_and_a_different_one_is_refused()
    {
        var (storage, bucket) = await NewStorageAsync();
        using var _ = storage;
        const string key = "t/abc/c/def/2026/01/F001-2/cdr-zip/v1";
        var first = await storage.PutAsync(key, Xml, "application/zip", CancellationToken.None);

        var again = await storage.PutAsync(key, Xml, "application/zip", CancellationToken.None);
        var different = await Assert.ThrowsAsync<ObjectAlreadyExistsException>(() => storage.PutAsync(key, Encoding.UTF8.GetBytes("<Invoice>otra</Invoice>"), "application/zip", CancellationToken.None));

        Assert.Equal(first.VersionId, again.VersionId); // nothing new was uploaded
        Assert.Equal(key, different.Key);
        Assert.Equal(Xml, await storage.GetAsync(key, first.Sha256, CancellationToken.None));
        using var raw = s3.RawClient();
        var versions = await raw.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key });
        Assert.Single(versions.Versions);
    }

    [Fact]
    public async Task Reading_a_missing_file_returns_null_and_a_file_altered_behind_the_platform_is_detected()
    {
        var (storage, bucket) = await NewStorageAsync();
        using var _ = storage;
        const string key = "t/abc/c/def/2026/01/F001-3/signed-xml/v1";

        Assert.Null(await storage.HeadAsync(key, CancellationToken.None));
        Assert.Null(await storage.GetAsync(key, Sha(Xml), CancellationToken.None));

        var stored = await storage.PutAsync(key, Xml, "application/xml", CancellationToken.None);
        var wrong = await Assert.ThrowsAsync<ObjectIntegrityException>(() => storage.GetAsync(key, Sha(Encoding.UTF8.GetBytes("otro")), CancellationToken.None));
        Assert.Equal(stored.Sha256, wrong.ActualSha256);

        // Somebody with access to the bucket replaces the file: the read no longer matches what the platform recorded.
        using var raw = s3.RawClient();
        await raw.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "<Invoice>alterada</Invoice>" });
        var altered = await Assert.ThrowsAsync<ObjectIntegrityException>(() => storage.GetAsync(key, stored.Sha256, CancellationToken.None));
        Assert.Equal(stored.Sha256, altered.ExpectedSha256);
    }

    [Fact]
    public async Task A_download_link_serves_the_file_without_credentials()
    {
        var (storage, _) = await NewStorageAsync();
        using var _ = storage;
        const string key = "t/abc/c/def/2026/01/F001-4/signed-xml/v1";
        await storage.PutAsync(key, Xml, "application/xml", CancellationToken.None);

        var url = await storage.CreateDownloadUrlAsync(key, TimeSpan.FromMinutes(5), CancellationToken.None);

        using var http = new HttpClient();
        Assert.Equal(Xml, await http.GetByteArrayAsync(url));
        Assert.Contains("Signature", url.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/absolute")]
    [InlineData("t/../other-tenant/file")]
    [InlineData("t/with\ncontrol")]
    public async Task Keys_that_could_escape_their_prefix_are_refused(string key)
    {
        var (storage, _) = await NewStorageAsync();
        using var _ = storage;

        await Assert.ThrowsAsync<ArgumentException>(() => storage.PutAsync(key, Xml, "application/xml", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.HeadAsync(key, CancellationToken.None));
    }

    [Fact]
    public void The_storage_settings_are_checked_when_the_host_starts()
    {
        static IServiceProvider Build(Dictionary<string, string?> settings)
        {
            var section = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            return new ServiceCollection().AddS3ObjectStorage(section).BuildServiceProvider();
        }

        static void Check(Dictionary<string, string?> settings) =>
            Assert.Throws<OptionsValidationException>(() => Build(settings).GetRequiredService<IOptions<S3StorageOptions>>().Value);

        var valid = new Dictionary<string, string?> { ["Bucket"] = "documents", ["ServiceUrl"] = "http://localhost:8333", ["AccessKey"] = "a", ["SecretKey"] = "b" };
        Assert.Equal("documents", Build(valid).GetRequiredService<IOptions<S3StorageOptions>>().Value.Bucket);

        Check(new Dictionary<string, string?>());
        Check(new Dictionary<string, string?>(valid) { ["Bucket"] = "" });
        Check(new Dictionary<string, string?>(valid) { ["ServiceUrl"] = "not a url" });
        Check(new Dictionary<string, string?>(valid) { ["SecretKey"] = "" }); // keys go together
        Check(new Dictionary<string, string?>(valid) { ["ServerSideEncryption"] = "Kms" }); // needs a key id
        Check(new Dictionary<string, string?>(valid) { ["ServerSideEncryption"] = "Rot13" });
        Check(new Dictionary<string, string?>(valid) { ["ObjectLockMode"] = "Compliance" }); // needs a retention
        Check(new Dictionary<string, string?>(valid) { ["ObjectLockMode"] = "Forever", ["RetentionDays"] = "10" });
    }

    [Fact]
    public async Task The_in_memory_storage_follows_the_same_rules()
    {
        var storage = new InMemoryObjectStorage();
        const string key = "t/abc/file";

        var stored = await storage.PutAsync(key, Xml, "application/xml", CancellationToken.None);
        Assert.Equal(stored, await storage.PutAsync(key, Xml, "application/xml", CancellationToken.None));
        await Assert.ThrowsAsync<ObjectAlreadyExistsException>(() => storage.PutAsync(key, new byte[] { 1, 2, 3 }, "application/xml", CancellationToken.None));
        Assert.Null(await storage.HeadAsync("other", CancellationToken.None));
        Assert.Null(await storage.GetAsync("other", stored.Sha256, CancellationToken.None));
        Assert.Equal(Xml, await storage.GetAsync(key, stored.Sha256, CancellationToken.None));
        Assert.StartsWith("memory://", (await storage.CreateDownloadUrlAsync(key, TimeSpan.FromMinutes(1), CancellationToken.None)).ToString(), StringComparison.Ordinal);

        storage.Tamper(key, [9, 9, 9]);
        await Assert.ThrowsAsync<ObjectIntegrityException>(() => storage.GetAsync(key, stored.Sha256, CancellationToken.None));
        Assert.Equal(1, storage.Count);
        Assert.Equal([key], storage.Keys);
    }
}
