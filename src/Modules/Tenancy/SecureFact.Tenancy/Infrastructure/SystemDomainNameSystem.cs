using System.Net;
using DnsClient;
using DnsClient.Protocol;
using SecureFact.Tenancy.Application;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Tenancy.Infrastructure;

/// <summary>Real DNS lookups, with the resolvers of the machine or the ones configured. A name that does not exist answers empty; a resolver that does not answer throws.</summary>
public sealed class SystemDomainNameSystem : IDomainNameSystem
{
    private readonly LookupClient _client;

    public SystemDomainNameSystem(DomainsOptions options)
    {
        // A check wants what the DNS says now, not what it said a while ago: no cache. An error of the DNS (a name that does not exist) is an empty answer, not an exception.
        var configured = options.Dns.Nameservers.Select(address => IPAddress.Parse(address)).ToArray();
        var settings = configured.Length > 0 ? new LookupClientOptions(configured) : new LookupClientOptions();
        settings.Timeout = TimeSpan.FromSeconds(5);
        settings.Retries = 1;
        settings.ThrowDnsErrors = false;
        settings.UseCache = false;
        _client = new LookupClient(settings);
    }

    public async Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken cancellationToken)
    {
        var response = await _client.QueryAsync(name, QueryType.TXT, cancellationToken: cancellationToken);
        return response.Answers.TxtRecords().Select(record => string.Concat(record.Text)).ToList();
    }

    public async Task<DomainRoute> RouteAsync(string name, CancellationToken cancellationToken)
    {
        // An A query goes through the aliases of the name: the answer holds the CNAME chain and the addresses at its end.
        var v4 = await _client.QueryAsync(name, QueryType.A, cancellationToken: cancellationToken);
        var v6 = await _client.QueryAsync(name, QueryType.AAAA, cancellationToken: cancellationToken);
        var cnames = v4.Answers.OfType<CNameRecord>().Concat(v6.Answers.OfType<CNameRecord>()).Select(record => record.CanonicalName.Value.TrimEnd('.')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var addresses = v4.Answers.ARecords().Select(record => record.Address.ToString()).Concat(v6.Answers.AaaaRecords().Select(record => record.Address.ToString())).Distinct().ToList();
        return new DomainRoute(cnames, addresses);
    }
}
