using SecureFact.SharedKernel.Results;

namespace SecureFact.Identity.Contracts;

public enum SupportGrantStatus
{
    Active,
    Expired,
    Revoked,
}

/// <summary>An authorization of an account for its service provider to read it for a while (ADR-069). <paramref name="Entries"/> counts the times someone entered with it.</summary>
public sealed record SupportGrantDto(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, string? Note, SupportGrantStatus Status, int Entries, DateTimeOffset? LastEntryAt);

/// <summary>An account that the person of the service provider may enter now: it authorized it and the authorization is still in force.</summary>
public sealed record AvailableSupportAccessDto(Guid TenantId, string TenantName, Guid GrantId, DateTimeOffset ExpiresAt);

/// <summary>
/// The session inside an account. It has an access token and nothing to renew it with: when it ends, the person goes back to their own session. <paramref name="ReadOnly"/> is always true in this version.
/// </summary>
public sealed record SupportSessionDto(string AccessToken, int ExpiresInSeconds, Guid TenantId, string TenantName, DateTimeOffset ExpiresAt, bool ReadOnly);

/// <summary>
/// Entering an account as the person who supports it (ADR-069). The account decides: its owner authorizes the access for a few hours and can take it back at any moment. Inside, the person
/// is themself (the audit names them), reads and changes nothing, and the owners are told every time someone enters.
/// </summary>
public interface ISupportAccess
{
    const int MinHours = 1;
    const int MaxHours = 72;
    const int DefaultHours = 24;

    /// <summary>The owner of an account authorizes its service provider to read it for <paramref name="hours"/> hours. One authorization at a time.</summary>
    Task<Result<SupportGrantDto>> GrantAsync(int? hours, string? note, CancellationToken cancellationToken);

    /// <summary>The authorizations of the account, the latest first.</summary>
    Task<Result<IReadOnlyList<SupportGrantDto>>> ListGrantsAsync(CancellationToken cancellationToken);

    /// <summary>The owner takes an authorization back: the sessions that were started with it end at once.</summary>
    Task<Result<SupportGrantDto>> RevokeAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>For the person who supports: the accounts they may enter now (all for the platform, only their customers for a reseller).</summary>
    Task<Result<IReadOnlyList<AvailableSupportAccessDto>>> AvailableAsync(CancellationToken cancellationToken);

    /// <summary>For the person who supports: starts the session inside an account that authorized it, saying why.</summary>
    Task<Result<SupportSessionDto>> EnterAsync(Guid tenantId, string reason, ClientInfo client, CancellationToken cancellationToken);
}
