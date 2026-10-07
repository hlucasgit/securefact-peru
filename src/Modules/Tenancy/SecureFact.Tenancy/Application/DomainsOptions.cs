using SecureFact.Tenancy.Application;

namespace SecureFact.Tenancy;

/// <summary>Settings of the domains of the resellers (<c>Domains</c> section). Nothing here is a secret except <see cref="EdgeSecret"/>, which comes from the environment.</summary>
public sealed class DomainsOptions
{
    public const string SectionName = "Domains";

    /// <summary>The name that a reseller's domain must be an alias (CNAME) of: the platform's edge, which terminates TLS. Empty: no alias is asked for.</summary>
    public string? EdgeHost { get; set; }

    /// <summary>Addresses of the edge, for a domain that cannot be an alias (the root of a zone): an A or AAAA record with these also routes it.</summary>
    public string[] EdgeAddresses { get; set; } = [];

    /// <summary>Hosts of the platform itself (its own web), which the edge may always issue a certificate for and that no reseller can take.</summary>
    public string[] PlatformHosts { get; set; } = [];

    /// <summary>The secret that the edge sends when it asks whether a name may have a certificate. Without one the question is not answered.</summary>
    public string? EdgeSecret { get; set; }

    public DnsSection Dns { get; set; } = new();

    public int MinimumCheckSeconds { get; set; } = 10;

    /// <summary>How soon a pending domain is checked again by the background pass.</summary>
    public int PendingRecheckMinutes { get; set; } = 2;

    /// <summary>How soon a verified or unreachable domain is checked again.</summary>
    public int SettledRecheckMinutes { get; set; } = 360;

    public int MaxPerPass { get; set; } = 50;

    /// <summary>Failed checks in a row before a verified domain is unreachable.</summary>
    public int UnreachableAfterFailures { get; set; } = 3;

    /// <summary>How often the worker runs a pass.</summary>
    public int WorkerIntervalSeconds { get; set; } = 60;

    public sealed class DnsSection
    {
        /// <summary><c>System</c> (real lookups) or <c>Sandbox</c> (every check passes: development and end-to-end tests, refused in production).</summary>
        public string Provider { get; set; } = "System";

        /// <summary>Resolvers to ask, as addresses. Empty: those of the machine.</summary>
        public string[] Nameservers { get; set; } = [];
    }

    internal bool Sandbox => string.Equals(Dns.Provider, "Sandbox", StringComparison.OrdinalIgnoreCase);

    internal TimeSpan MinimumCheckInterval => TimeSpan.FromSeconds(MinimumCheckSeconds);

    internal TimeSpan PendingRecheck => TimeSpan.FromMinutes(PendingRecheckMinutes);

    internal TimeSpan SettledRecheck => TimeSpan.FromMinutes(SettledRecheckMinutes);

    /// <summary>A name that is the platform's own: its hosts and its edge.</summary>
    internal bool IsPlatformName(string? host) =>
        HostNames.Normalize(host) is { } name && (PlatformHosts.Any(p => HostNames.Normalize(p) == name) || HostNames.Normalize(EdgeHost) == name);
}
