using SecureFact.SharedKernel.Domain;

namespace SecureFact.SharedKernel.Tenancy;

/// <summary>
/// Authenticated principal of the current request, derived only from validated credentials.
/// Platform staff have no tenant; tenant users always have one.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    Guid? SessionId { get; }

    TenantId? TenantId { get; }

    bool IsPlatform { get; }

    /// <summary>The reseller of a reseller user (from the signed token); null for everyone else.</summary>
    Guid? ResellerId { get; }

    IReadOnlySet<string> Roles { get; }

    IReadOnlySet<string> Permissions { get; }

    bool HasPermission(string permission);
}
