using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Identity.Contracts;

/// <summary>An API key as it is listed. The secret is never part of it: it is shown once, when the key is created.</summary>
/// <param name="Prefix">The first characters of the secret, to tell the keys apart in a list.</param>
public sealed record ApiKeyDto(Guid Id, string Name, string Role, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);

/// <param name="Secret">The key itself, to put in <c>Authorization: Bearer …</c>. It is returned only here and cannot be read again.</param>
public sealed record CreatedApiKey(ApiKeyDto Key, string Secret);

/// <summary>Who an API key stands for once it is verified: its tenant and the role whose permissions it has.</summary>
public sealed record ApiKeyIdentity(Guid KeyId, Guid TenantId, string Role);

/// <summary>
/// Keys for programs that call the API without a person signing in (ADR-066). A key belongs to a tenant and acts with the permissions of one role of the tenant, never more than the person who
/// created it holds and never those of an administrator: a key cannot manage users, keys or webhooks. Only the SHA-256 of the secret is kept.
/// </summary>
public interface IApiKeys
{
    /// <summary>The roles a key may take. The roles that manage the tenant are not among them.</summary>
    static readonly IReadOnlyList<string> AssignableRoles = [Roles.BillingAdmin, Roles.Sales, Roles.Accountant, Roles.Auditor, Roles.ReadOnly];

    /// <summary>The prefix of every key, which tells a key from an access token in the same header.</summary>
    const string Prefix = "sfk_";

    Task<Result<CreatedApiKey>> CreateAsync(string name, string role, DateTimeOffset? expiresAt, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<ApiKeyDto>>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Revokes a key of the tenant. It stops working at once and cannot be restored.</summary>
    Task<Result<ApiKeyDto>> RevokeAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Checks what a caller presented. Null when it is not a key, does not exist, was revoked, expired or its secret does not match: the caller cannot tell which, nor whether the id exists.
    /// A key that works is marked as used (at most once in a few minutes).
    /// </summary>
    Task<ApiKeyIdentity?> AuthenticateAsync(string presented, CancellationToken cancellationToken);
}
