using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Platform.Security;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;
using SecureFact.Subscriptions.Contracts;
using SecureFact.Subscriptions.Domain;
using SecureFact.Subscriptions.Infrastructure;

namespace SecureFact.Subscriptions.Application;

internal sealed partial class GatewaySettingsService(
    SubscriptionsDbContext db, IDataScope scope, TimeProvider clock, IAuditTrail audit, ISecretProtector secrets, Microsoft.Extensions.Options.IOptions<PaymentOptions> options) : IGatewaySettings
{
    private const int MaxListed = 100;
    private const int MinNoteLength = 3;
    private const int MaxNoteLength = 300;
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);

    private static readonly Error PlatformOnly = Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "La pasarela de pago la administra la plataforma.");

    public async Task<Result<GatewaySettingsDto>> GetAsync(CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Platform)
        {
            return PlatformOnly;
        }

        var setting = await db.GatewaySettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return setting is null
            ? new GatewaySettingsDto(PaymentGatewayProvider.Culqi, null, false, "test", false, DateTimeOffset.MinValue)
            : ToDto(setting);
    }

    public async Task<Result<GatewaySettingsDto>> SetAsync(GatewaySettingsInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (scope.Kind != DataScopeKind.Platform)
        {
            return PlatformOnly;
        }

        if (!Enum.IsDefined(input.Provider))
        {
            return Invalid("La pasarela no existe.");
        }

        var existing = await db.GatewaySettings.SingleOrDefaultAsync(cancellationToken);
        string? publicKey = null;
        byte[]? protectedSecret = null;
        if (input.Provider == PaymentGatewayProvider.Sandbox)
        {
            if (!options.Value.AllowSandbox)
            {
                return Invalid("El simulador de la pasarela no se admite en este ambiente.");
            }
        }
        else
        {
            publicKey = input.PublicKey?.Trim();
            if (publicKey is null || !PublicKeyPattern().IsMatch(publicKey))
            {
                return Invalid("La llave pública de Culqi empieza con pk_test_ o pk_live_.");
            }

            var secret = input.SecretKey?.Trim();
            if (!string.IsNullOrEmpty(secret))
            {
                if (!SecretKeyPattern().IsMatch(secret))
                {
                    return Invalid("La llave secreta de Culqi empieza con sk_test_ o sk_live_.");
                }

                if (!SameEnvironment(publicKey, secret))
                {
                    return Invalid("Las llaves pública y secreta son de ambientes distintos (una de prueba y otra de producción).");
                }

                protectedSecret = secrets.Protect(Encoding.UTF8.GetBytes(secret), GatewayAccess.SecretPurpose);
            }
            else if (existing is { Provider: PaymentGatewayProvider.Culqi, ProtectedSecretKey: { } kept })
            {
                // The secret that is stored stays, but only for the public key of the same environment: another environment needs its own.
                var stored = Encoding.UTF8.GetString(secrets.Unprotect(kept, GatewayAccess.SecretPurpose));
                if (!SameEnvironment(publicKey, stored))
                {
                    return Invalid("La llave secreta guardada es de otro ambiente: ingrese también la llave secreta que corresponde.");
                }

                protectedSecret = kept;
            }

            if (input.Enabled && protectedSecret is null)
            {
                return Invalid("Para activar los pagos con Culqi hacen falta la llave pública y la secreta.");
            }
        }

        var now = clock.GetUtcNow();
        if (existing is null)
        {
            existing = GatewaySetting.Create(input.Provider, publicKey, protectedSecret, input.Enabled, now);
            db.GatewaySettings.Add(existing);
        }
        else
        {
            existing.Apply(input.Provider, publicKey, protectedSecret, input.Enabled, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        var dto = ToDto(existing);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.PaymentGatewayConfigured, "payment_gateway", null, null,
                NewValues: new Dictionary<string, object?> { ["provider"] = dto.Provider.ToString(), ["environment"] = dto.Environment, ["enabled"] = dto.Enabled, ["publicKey"] = dto.PublicKey, ["hasSecretKey"] = dto.HasSecretKey }),
            cancellationToken);
        return dto;
    }

    public async Task<Result<IReadOnlyList<PaymentAttemptDto>>> ListUnresolvedAsync(CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Platform)
        {
            return PlatformOnly;
        }

        var stuck = clock.GetUtcNow() - StuckAfter;
        var rows = await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.State == PaymentAttemptState.UnderReview || (a.State == PaymentAttemptState.Started && a.CreatedAt < stuck))
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).Take(MaxListed).ToListAsync(cancellationToken);
        var charges = await db.Charges.AsNoTracking().Where(c => rows.Select(a => a.ChargeId).Contains(c.Id)).Select(c => new { c.Id, c.Period, c.TenantName }).ToDictionaryAsync(c => c.Id, cancellationToken);
        return rows.Select(a => ToDto(a, charges.GetValueOrDefault(a.ChargeId)?.TenantName ?? string.Empty, charges.TryGetValue(a.ChargeId, out var c) ? LimaCalendar.PeriodName(c.Period) : string.Empty)).ToList();
    }

    public async Task<Result<PaymentAttemptDto>> ResolveAsync(Guid attemptId, string note, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Platform)
        {
            return PlatformOnly;
        }

        var text = note?.Trim() ?? string.Empty;
        if (text.Length is < MinNoteLength or > MaxNoteLength)
        {
            return Error.Validation(ErrorCodes.InvalidPaymentGateway, "Nota inválida", $"Diga qué se revisó y qué se hizo, entre {MinNoteLength} y {MaxNoteLength} caracteres.");
        }

        var attempt = await db.PaymentAttempts.SingleOrDefaultAsync(a => a.Id == attemptId, cancellationToken);
        var now = clock.GetUtcNow();
        if (attempt is null)
        {
            return Error.NotFound(ErrorCodes.PaymentAttemptNotFound, "Intento no encontrado", "El intento de pago no existe.");
        }

        if (attempt.State != PaymentAttemptState.UnderReview && !(attempt.State == PaymentAttemptState.Started && attempt.CreatedAt < now - StuckAfter))
        {
            return Error.Conflict(ErrorCodes.InvalidPaymentGateway, "Intento sin revisión pendiente", "Solo se cierra un intento que espera revisión.");
        }

        attempt.Resolve(text, now);
        await db.SaveChangesAsync(cancellationToken);
        var charge = await db.Charges.AsNoTracking().Where(c => c.Id == attempt.ChargeId).Select(c => new { c.Period, c.TenantName }).SingleOrDefaultAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(AuditActions.PaymentAttemptResolved, "payment_attempt", attempt.Id.ToString("D"), attempt.TenantId, NewValues: new Dictionary<string, object?> { ["charge"] = attempt.ChargeId, ["note"] = text }),
            cancellationToken);
        return ToDto(attempt, charge?.TenantName ?? string.Empty, charge is null ? string.Empty : LimaCalendar.PeriodName(charge.Period));
    }

    private static bool SameEnvironment(string publicKey, string secretKey) => publicKey.StartsWith("pk_live_", StringComparison.Ordinal) == secretKey.StartsWith("sk_live_", StringComparison.Ordinal);

    private static Error Invalid(string detail) => Error.Validation(ErrorCodes.InvalidPaymentGateway, "Configuración de la pasarela inválida", detail);

    private static GatewaySettingsDto ToDto(GatewaySetting s)
    {
        var runtime = new GatewayRuntime(s.Provider, s.PublicKey, null, s.Enabled);
        return new GatewaySettingsDto(s.Provider, s.PublicKey, s.ProtectedSecretKey is not null, runtime.Environment, s.Enabled, s.UpdatedAt);
    }

    private static PaymentAttemptDto ToDto(PaymentAttempt a, string tenantName, string period) =>
        new(a.Id, a.TenantId, tenantName, a.ChargeId, period, a.Amount, a.State, a.GatewayChargeId, a.Code, a.Message, a.CreatedAt, a.CompletedAt, a.Resolution);

    [GeneratedRegex("^pk_(test|live)_[A-Za-z0-9]{8,64}$")]
    private static partial Regex PublicKeyPattern();

    [GeneratedRegex("^sk_(test|live)_[A-Za-z0-9]{8,64}$")]
    private static partial Regex SecretKeyPattern();
}
