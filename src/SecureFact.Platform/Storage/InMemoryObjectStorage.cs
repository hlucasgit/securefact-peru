using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using SecureFact.SharedKernel.Storage;

namespace SecureFact.Platform.Storage;

/// <summary>
/// <see cref="IObjectStorage"/> in memory, with the same rules as the real one (write once, hash checked on read). For tests and for a development machine without an S3 server; the
/// hosts refuse to use it in production.
/// </summary>
public sealed class InMemoryObjectStorage : IObjectStorage
{
    private readonly ConcurrentDictionary<string, (byte[] Content, StoredObject Info)> _objects = new(StringComparer.Ordinal);

    /// <summary>Number of files held, for tests.</summary>
    public int Count => _objects.Count;

    public IReadOnlyCollection<string> Keys => [.. _objects.Keys];

    public Task<StoredObject> PutAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        var info = new StoredObject(key, "1", hash, content.Length);
        var stored = _objects.GetOrAdd(key, _ => (content.ToArray(), info));
        return stored.Info.Sha256 == hash ? Task.FromResult(stored.Info) : throw new ObjectAlreadyExistsException(key);
    }

    public Task<StoredObject?> HeadAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_objects.TryGetValue(key, out var stored) ? stored.Info : null);

    public Task<byte[]?> GetAsync(string key, string expectedSha256, CancellationToken cancellationToken)
    {
        if (!_objects.TryGetValue(key, out var stored))
        {
            return Task.FromResult<byte[]?>(null);
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(stored.Content));
        return actual == expectedSha256 ? Task.FromResult<byte[]?>(stored.Content.ToArray()) : throw new ObjectIntegrityException(key, expectedSha256, actual);
    }

    public Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan validFor, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"memory://objects/{Uri.EscapeDataString(key)}"));

    /// <summary>Replaces a stored file behind the back of the rules, to test that a read notices it.</summary>
    public void Tamper(string key, byte[] content) =>
        _objects[key] = (content, _objects[key].Info);
}

public static class StorageServiceCollectionExtensions
{
    /// <summary>Registers the in-memory storage as the <see cref="IObjectStorage"/> of the host. For development and tests only.</summary>
    public static IServiceCollection AddInMemoryObjectStorage(this IServiceCollection services) =>
        services.AddSingleton<InMemoryObjectStorage>().AddSingleton<IObjectStorage>(sp => sp.GetRequiredService<InMemoryObjectStorage>());
}
