using SecureFact.SharedKernel.Results;

namespace SecureFact.Identity.Contracts;

public sealed record ClientInfo(string? IpAddress, string? UserAgent);

public sealed record LoginRequest(string Email, string Password, string? TotpCode = null);

public sealed record AuthTokens(string AccessToken, string RefreshToken, int ExpiresInSeconds, string TokenType = "Bearer");

public sealed record MfaEnrollment(string Secret, string OtpAuthUri);

/// <summary><paramref name="ResellerId"/> names the reseller of a ResellerAdmin user; it is required for that role and refused for any other.</summary>
public sealed record CreateUserRequest(string Email, string DisplayName, string Password, IReadOnlyList<string> Roles, Guid? TenantId = null, Guid? ResellerId = null);

public sealed record UserDto(
    Guid Id,
    Guid? TenantId,
    Guid? ResellerId,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool IsActive,
    bool MfaEnabled,
    DateTimeOffset CreatedAt);

public interface IAuthenticationService
{
    Task<Result<AuthTokens>> LoginAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken);

    /// <summary>Rotates the refresh token. Reusing an already-rotated token revokes the whole session family.</summary>
    Task<Result<AuthTokens>> RefreshAsync(string refreshToken, ClientInfo client, CancellationToken cancellationToken);

    Task<Result<Unit>> LogoutAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<Result<MfaEnrollment>> BeginMfaEnrollmentAsync(Guid userId, CancellationToken cancellationToken);

    Task<Result<Unit>> ConfirmMfaEnrollmentAsync(Guid userId, string totpCode, CancellationToken cancellationToken);

    /// <summary>True when the session exists, is not revoked and has not expired. Used on every authenticated request.</summary>
    Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken cancellationToken);
}

public interface IUserAdministration
{
    Task<Result<UserDto>> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken);

    Task<Result<UserDto>> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The users of the current scope. Platform staff may name a tenant to see only its users; a tenant user always sees its own tenant.</summary>
    Task<IReadOnlyList<UserDto>> ListAsync(int skip, int take, Guid? tenantId, CancellationToken cancellationToken);

    /// <summary>How many active users the tenant has (for plan consumption). Platform staff may ask about any tenant; a tenant user only about its own.</summary>
    Task<int> CountActiveAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the first owner of a tenant that a reseller has just opened. It needs no role of the actor beyond the reseller permission that the endpoint demands, because a reseller
    /// holds none of the permissions of the owner it creates; the tenant must be one of its own, which the caller has already checked.
    /// </summary>
    Task<Result<UserDto>> CreateTenantOwnerAsync(Guid tenantId, string email, string displayName, string password, CancellationToken cancellationToken);

    Task<Result<UserDto>> AssignRoleAsync(Guid userId, string role, CancellationToken cancellationToken);

    Task<Result<UserDto>> RemoveRoleAsync(Guid userId, string role, CancellationToken cancellationToken);

    Task<Result<Unit>> DeactivateAsync(Guid userId, CancellationToken cancellationToken);

    Task<Result<Unit>> RevokeSessionsAsync(Guid userId, CancellationToken cancellationToken);
}

public interface IPasswordResetService
{
    /// <summary>Always succeeds from the caller's view so existence of an account is never revealed.</summary>
    Task<Result<Unit>> RequestAsync(string email, CancellationToken cancellationToken);

    Task<Result<Unit>> ConfirmAsync(string token, string newPassword, CancellationToken cancellationToken);
}

/// <summary>
/// What the notifier needs to deliver a reset: the address, the one-time token, when it stops working, and where the account belongs (its tenant, or its reseller for a reseller user),
/// so the message carries the brand of the reseller (ADR-052).
/// </summary>
public sealed record PasswordResetDelivery(string Email, string Token, DateTimeOffset ExpiresAt, Guid? TenantId, Guid? ResellerId);

/// <summary>Tells the account holder that the password of the account changed, so a change they did not make is noticed (ADR-052).</summary>
public sealed record PasswordChangedNotice(string Email, DateTimeOffset ChangedAt, Guid? TenantId, Guid? ResellerId);

/// <summary>Delivers the one-time reset token to the account holder. The token must never be logged or returned by the API.</summary>
public interface IPasswordResetNotifier
{
    Task SendAsync(PasswordResetDelivery delivery, CancellationToken cancellationToken);

    /// <summary>Warns the holder that the password changed. A failed delivery never undoes the change.</summary>
    Task NotifyChangedAsync(PasswordChangedNotice notice, CancellationToken cancellationToken);
}

/// <summary>An account was created for a person (ADR-054). There is no password in it: whoever created the account gave it.</summary>
public sealed record AccountCreatedNotice(string Email, string DisplayName, Guid? TenantId, Guid? ResellerId, IReadOnlyList<string> Roles);

/// <summary>Tells the person that an account exists for them. A failed notice never undoes the creation.</summary>
public interface IAccountNotices
{
    Task AccountCreatedAsync(AccountCreatedNotice notice, CancellationToken cancellationToken);
}

/// <summary>Who to write to: the e-mail addresses of the active people who hold a role in an account or in a reseller. Read-only, for the notices (ADR-054).</summary>
public interface IAccountDirectory
{
    Task<IReadOnlyList<string>> TenantOwnerEmailsAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ResellerAdminEmailsAsync(Guid resellerId, CancellationToken cancellationToken);
}

/// <summary>One-time creation of the first platform administrator (run from the operator command line, never from HTTP).</summary>
public interface IPlatformBootstrapper
{
    /// <returns>True when the administrator was created, false when one already exists.</returns>
    Task<Result<bool>> EnsureFirstPlatformAdminAsync(string email, string password, CancellationToken cancellationToken);
}
