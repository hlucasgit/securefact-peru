using System.Collections.Concurrent;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>The DNS of the tests: it answers only what a test has published, and can be made to fail as a resolver that does not answer.</summary>
public sealed class FakeDomainNameSystem : IDomainNameSystem
{
    public const string EdgeHost = "edge.securefact.test";

    public const string EdgeAddress = "203.0.113.10";

    public const string PlatformHost = "app.securefact.test";

    public const string EdgeSecret = "edge-secret-for-tests";

    private readonly ConcurrentDictionary<string, List<string>> _txt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DomainRoute> _routes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, every lookup throws: the resolver does not answer.</summary>
    private volatile bool _down;

    public bool Down
    {
        get => _down;
        set => _down = value;
    }

    public void PublishTxt(string name, string value) => _txt.AddOrUpdate(name, _ => [value], (_, list) => { list.Add(value); return list; });

    public void Unpublish(string name) => _txt.TryRemove(name, out _);

    public void PointAt(string host, params string[] cnames) => _routes[host] = new DomainRoute(cnames, []);

    public void ResolveTo(string host, params string[] addresses) => _routes[host] = new DomainRoute([], addresses);

    public void Forget(string host) => _routes.TryRemove(host, out _);

    public Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken cancellationToken) =>
        Down ? throw new TimeoutException("resolver down") : Task.FromResult<IReadOnlyList<string>>(_txt.TryGetValue(name, out var list) ? [.. list] : []);

    public Task<DomainRoute> RouteAsync(string name, CancellationToken cancellationToken) =>
        Down ? throw new TimeoutException("resolver down") : Task.FromResult(_routes.TryGetValue(name, out var route) ? route : new DomainRoute([], []));
}
