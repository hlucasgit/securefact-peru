using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SecureFact.Audit.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Identity.Domain;
using SecureFact.Identity.Infrastructure;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Identity.Application;

internal sealed class PasswordResetService(
    IdentityDbContext db,
    DataScope scope,
    PasswordHasher hasher,
    IPasswordResetNotifier notifier,
    IOptions<IdentityOptions> options,
    TimeProvider clock,
    IAuditTrail audit) : IPasswordResetService
{
    private static readonly Error InvalidToken = Error.Validation(
        ErrorCodes.InvalidResetToken, "Enlace inválido", "El enlace de recuperación no es válido o expiró.");

    public async Task<Result<Unit>> RequestAsync(string email, CancellationToken cancellationToken)
    {
        using var elevated = scope.Elevate("identity:password-reset-request");
        var normalized = User.Normalize(email ?? string.Empty);
        var user = await db.Users.SingleOrDefaultAsync(u => u.EmailNormalized == normalized && u.IsActive, cancellationToken);
        if (user is null)
        {
            return Unit.Value;
        }

        var now = clock.GetUtcNow();
        var pending = await db.PasswordResetTokens.Where(t => t.UserId == user.Id && t.UsedAt == null).ToListAsync(cancellationToken);
        foreach (var old in pending)
        {
            old.MarkUsed(now);
        }

        var token = TokenService.NewRefreshToken();
        var expiresAt = now.AddMinutes(options.Value.PasswordResetMinutes);
        db.PasswordResetTokens.Add(PasswordResetToken.Issue(user.Id, user.TenantId, TokenService.HashRefreshToken(token), now, expiresAt));
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(new AuditEvent(AuditActions.PasswordResetRequested, "user", user.Id.ToString("D"), user.TenantId, ActorUserId: user.Id), cancellationToken);
        await notifier.SendAsync(user.Email, token, expiresAt, cancellationToken);
        return Unit.Value;
    }

    public async Task<Result<Unit>> ConfirmAsync(string token, string newPassword, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return InvalidToken;
        }

        using var elevated = scope.Elevate("identity:password-reset-confirm");
        var now = clock.GetUtcNow();
        var hash = TokenService.HashRefreshToken(token);
        var record = await db.PasswordResetTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (record is null || !record.IsUsable(now))
        {
            return InvalidToken;
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == record.UserId && u.IsActive, cancellationToken);
        if (user is null)
        {
            return InvalidToken;
        }

        if (PasswordPolicy.Validate(newPassword, user.Email) is { } weak)
        {
            return weak;
        }

        user.ChangePassword(hasher.Hash(newPassword), now);
        record.MarkUsed(now);
        var sessions = await db.Sessions.Where(s => s.UserId == user.Id && s.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.Revoke("password-reset", now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.PasswordResetCompleted, "user", user.Id.ToString("D"), user.TenantId, ActorUserId: user.Id), cancellationToken);
        return Unit.Value;
    }
}
