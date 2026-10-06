using SecureFact.SharedKernel.Results;

namespace SecureFact.Identity.Contracts;

public sealed record ClientInfo(string? IpAddress, string? UserAgent);

public sealed record LoginRequest(string Email, string Password, string? TotpCode = null);

public sealed record AuthTokens(string AccessToken, string RefreshToken, int ExpiresInSeconds, string TokenType = "Bearer");

public sealed record MfaEnrollment(string Secret, string OtpAuthUri);

public sealed record CreateUserRequest(string Email, string DisplayName, string Password, IReadOnlyList<string> Roles, Guid? TenantId = null);

public sealed record UserDto(
    Guid Id,
    Guid? TenantId,
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

/// <summary>Delivers the one-time reset token to the account holder. The token must never be logged or returned by the API.</summary>
public interface IPasswordResetNotifier
{
    Task SendAsync(string email, string token, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}

/// <summary>One-time creation of the first platform administrator (run from the operator command line, never from HTTP).</summary>
public interface IPlatformBootstrapper
{
    /// <returns>True when the administrator was created, false when one already exists.</returns>
    Task<Result<bool>> EnsureFirstPlatformAdminAsync(string email, string password, CancellationToken cancellationToken);
}
