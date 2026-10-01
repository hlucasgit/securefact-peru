using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecureFact.Audit.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Identity.Domain;
using SecureFact.Identity.Infrastructure;
using SecureFact.Platform.Security;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Telemetry;

namespace SecureFact.Identity.Application;

internal sealed partial class AuthenticationService(
    IdentityDbContext db,
    DataScope scope,
    PasswordHasher hasher,
    TokenService tokens,
    ISecretProtector secrets,
    IOptions<IdentityOptions> options,
    TimeProvider clock,
    IAuditTrail audit,
    ILogger<AuthenticationService> logger) : IAuthenticationService
{
    private static readonly Error InvalidCredentials = Error.Validation(
        ErrorCodes.InvalidCredentials, "Credenciales inválidas", "El correo o la contraseña no son correctos.");

    private static readonly Error InvalidRefresh = Error.Validation(
        ErrorCodes.InvalidRefreshToken, "Sesión inválida", "La sesión expiró o ya no es válida. Inicie sesión nuevamente.");

    private IdentityOptions Options => options.Value;

    public async Task<Result<AuthTokens>> LoginAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        // The tenant is unknown until the account is found, so the lookup runs in an explicit, narrow platform scope.
        using var elevated = scope.Elevate("identity:login");
        var now = clock.GetUtcNow();

        var normalized = User.Normalize(request.Email ?? string.Empty);
        var user = await db.Users.Include(u => u.Roles).SingleOrDefaultAsync(u => u.EmailNormalized == normalized, cancellationToken);

        if (user is null)
        {
            hasher.VerifyDummy(request.Password ?? string.Empty);
            return InvalidCredentials;
        }

        if (!user.IsActive || user.IsLocked(now))
        {
            hasher.VerifyDummy(request.Password ?? string.Empty);
            LogRejected(user.Id, user.IsActive ? "locked" : "inactive");
            return InvalidCredentials;
        }

        if (!PasswordHasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
        {
            await RegisterFailureAsync(user, now, cancellationToken);
            return InvalidCredentials;
        }

        if (user.MfaEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.TotpCode))
            {
                SecureFactTelemetry.Logins.Add(1, new KeyValuePair<string, object?>("outcome", "mfa_required"));
                return Error.Validation(ErrorCodes.MfaRequired, "Segundo factor requerido", "Ingrese el código de su aplicación de autenticación.");
            }

            var step = Totp.Verify(secrets.Unprotect(user.MfaSecret!, MfaPurpose(user.Id)), request.TotpCode, now);
            if (step is null || !user.TryAcceptMfaStep(step.Value, now))
            {
                await RegisterFailureAsync(user, now, cancellationToken);
                return Error.Validation(ErrorCodes.InvalidMfaCode, "Código inválido", "El código de verificación no es correcto.");
            }
        }

        user.RegisterSuccessfulLogin(now);
        var (refreshToken, session) = StartSession(user, Guid.CreateVersion7(), now, null, client);
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        SecureFactTelemetry.Logins.Add(1, new KeyValuePair<string, object?>("outcome", "succeeded"));
        await RecordAsync(AuditActions.LoginSucceeded, user, new Dictionary<string, object?> { ["sessionId"] = session.Id, ["mfa"] = user.MfaEnabled }, cancellationToken);

        return BuildTokens(user, session, refreshToken);
    }

    public async Task<Result<AuthTokens>> RefreshAsync(string refreshToken, ClientInfo client, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return InvalidRefresh;
        }

        using var elevated = scope.Elevate("identity:refresh");
        var now = clock.GetUtcNow();
        var hash = TokenService.HashRefreshToken(refreshToken);

        var session = await db.Sessions.SingleOrDefaultAsync(s => s.RefreshHash == hash, cancellationToken);
        if (session is null)
        {
            return InvalidRefresh;
        }

        if (session.RevokedAt is not null)
        {
            // A rotated or revoked token is being presented again: treat as theft and kill the whole family.
            if (session.ReplacedBy is not null)
            {
                await RevokeFamilyAsync(session.FamilyId, "reuse-detected", now, cancellationToken);
                LogReuse(session.UserId, session.FamilyId);
                SecureFactTelemetry.RefreshReuse.Add(1);
                await audit.RecordAsync(
                    new AuditEvent(
                        AuditActions.RefreshReuseDetected, "session", session.Id.ToString("D"), session.TenantId,
                        NewValues: new Dictionary<string, object?> { ["familyId"] = session.FamilyId }, ActorUserId: session.UserId),
                    cancellationToken);
            }

            return InvalidRefresh;
        }

        if (!session.IsActive(now))
        {
            return InvalidRefresh;
        }

        var user = await db.Users.Include(u => u.Roles).SingleOrDefaultAsync(u => u.Id == session.UserId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            await RevokeFamilyAsync(session.FamilyId, "user-inactive", now, cancellationToken);
            return InvalidRefresh;
        }

        var (newRefresh, next) = StartSession(user, session.FamilyId, now, session.AbsoluteExpiresAt, client);
        session.Revoke("rotated", now, next.Id);
        db.Sessions.Add(next);
        await db.SaveChangesAsync(cancellationToken);

        return BuildTokens(user, next, newRefresh);
    }

    public async Task<Result<Unit>> LogoutAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is not null)
        {
            await RevokeFamilyAsync(session.FamilyId, "logout", clock.GetUtcNow(), cancellationToken);
            await audit.RecordAsync(new AuditEvent(AuditActions.Logout, "session", session.Id.ToString("D"), session.TenantId, ActorUserId: session.UserId), cancellationToken);
        }

        return Unit.Value;
    }

    public async Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return await db.Sessions.AsNoTracking()
            .AnyAsync(s => s.Id == sessionId && s.RevokedAt == null && s.ExpiresAt > now && s.AbsoluteExpiresAt > now, cancellationToken);
    }

    public async Task<Result<MfaEnrollment>> BeginMfaEnrollmentAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return Error.NotFound(ErrorCodes.UserNotFound, "Usuario no encontrado", "El usuario no existe.");
        }

        if (user.MfaEnabled)
        {
            return Error.Conflict(ErrorCodes.InvalidRequest, "MFA ya activo", "El segundo factor ya está activo para este usuario.");
        }

        var secret = Totp.NewSecret();
        user.StartMfaEnrollment(secrets.Protect(secret, MfaPurpose(user.Id)), clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);

        var base32 = Totp.ToBase32(secret);
        var issuer = Uri.EscapeDataString(Options.TotpIssuer);
        var uri = $"otpauth://totp/{issuer}:{Uri.EscapeDataString(user.Email)}?secret={base32}&issuer={issuer}&digits={Totp.Digits}&period={Totp.StepSeconds}";
        return new MfaEnrollment(base32, uri);
    }

    public async Task<Result<Unit>> ConfirmMfaEnrollmentAsync(Guid userId, string totpCode, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user?.MfaSecret is null || user.MfaEnabled)
        {
            return Error.Validation(ErrorCodes.InvalidMfaCode, "Código inválido", "No hay una inscripción de segundo factor pendiente.");
        }

        var now = clock.GetUtcNow();
        var step = Totp.Verify(secrets.Unprotect(user.MfaSecret, MfaPurpose(user.Id)), totpCode, now);
        if (step is null)
        {
            return Error.Validation(ErrorCodes.InvalidMfaCode, "Código inválido", "El código de verificación no es correcto.");
        }

        user.EnableMfa(step.Value, now);
        await db.SaveChangesAsync(cancellationToken);
        await RecordAsync(AuditActions.MfaEnabled, user, null, cancellationToken);
        return Unit.Value;
    }

    internal static string MfaPurpose(Guid userId) => $"identity:totp:{userId:D}";

    private async Task RegisterFailureAsync(User user, DateTimeOffset now, CancellationToken cancellationToken)
    {
        user.RegisterFailedAttempt(now, Options.MaxFailedAttempts, TimeSpan.FromMinutes(Options.LockoutMinutes));
        await db.SaveChangesAsync(cancellationToken);
        SecureFactTelemetry.Logins.Add(1, new KeyValuePair<string, object?>("outcome", user.IsLocked(now) ? "locked" : "failed"));
        await RecordAsync(AuditActions.LoginFailed, user, null, cancellationToken);
        if (user.IsLocked(now))
        {
            LogLocked(user.Id);
            await RecordAsync(AuditActions.AccountLocked, user, new Dictionary<string, object?> { ["lockedUntil"] = user.LockedUntil }, cancellationToken);
        }
    }

    private Task RecordAsync(string action, User user, IReadOnlyDictionary<string, object?>? values, CancellationToken cancellationToken) =>
        audit.RecordAsync(new AuditEvent(action, "user", user.Id.ToString("D"), user.TenantId, NewValues: values, ActorUserId: user.Id), cancellationToken);

    private async Task RevokeFamilyAsync(Guid familyId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var family = await db.Sessions.Where(s => s.FamilyId == familyId && s.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var s in family)
        {
            s.Revoke(reason, now);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private (string RefreshToken, UserSession Session) StartSession(User user, Guid familyId, DateTimeOffset now, DateTimeOffset? absoluteExpiresAt, ClientInfo client)
    {
        var refreshToken = TokenService.NewRefreshToken();
        var absolute = absoluteExpiresAt ?? now.AddDays(Options.SessionAbsoluteDays);
        var idleExpiry = now.AddDays(Options.RefreshTokenDays);
        var session = UserSession.Start(
            Guid.CreateVersion7(), user.Id, user.TenantId, familyId, TokenService.HashRefreshToken(refreshToken),
            now, idleExpiry < absolute ? idleExpiry : absolute, absolute, client.IpAddress, client.UserAgent);
        return (refreshToken, session);
    }

    private AuthTokens BuildTokens(User user, UserSession session, string refreshToken)
    {
        var (access, expiresIn) = tokens.IssueAccessToken(user, session.Id);
        return new AuthTokens(access, refreshToken, expiresIn);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Login rejected for user {UserId}: account {Reason}")]
    private partial void LogRejected(Guid userId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "User {UserId} locked after repeated failed attempts")]
    private partial void LogLocked(Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refresh token reuse detected for user {UserId}; session family {FamilyId} revoked")]
    private partial void LogReuse(Guid userId, Guid familyId);
}
