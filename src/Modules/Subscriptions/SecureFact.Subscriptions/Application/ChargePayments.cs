using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SecureFact.Audit.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Subscriptions.Contracts;
using SecureFact.Subscriptions.Domain;
using SecureFact.Subscriptions.Infrastructure;

namespace SecureFact.Subscriptions.Application;

/// <summary>
/// Paying a charge with a card (ADR-073). The order is the whole point: the attempt is written before the gateway is asked, so a charge that was made and whose answer was lost leaves a trace; the
/// gateway is asked outside any transaction; the payment is recorded only after the gateway says it was made; and what cannot be told is left to a person and never tried again by itself.
/// </summary>
internal sealed partial class ChargePayments(
    SubscriptionsDbContext db,
    IDataScope scope,
    ICurrentUser actor,
    TimeProvider clock,
    IAuditTrail audit,
    PlatformAccess platform,
    IEnumerable<IPaymentGateway> gateways,
    ILogger<ChargePayments> logger) : IChargePayments
{
    private const int MinCents = 100;
    private const int MaxCents = 9_999_900;

    private static readonly Error TenantOnly = Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "El pago en línea es de una cuenta: lo hace una persona de la cuenta que debe el cargo.");

    private static readonly Error Unavailable = Error.Validation(ErrorCodes.PaymentsUnavailable, "Pago en línea no disponible", "El pago con tarjeta no está disponible por ahora. Pague por transferencia o consulte con quien le da el servicio.");

    public async Task<Result<PaymentConfigDto>> GetConfigAsync(CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Tenant || actor.IsSupportAccess)
        {
            return TenantOnly;
        }

        var gateway = await platform.RunAsync("subscriptions: payment gateway config", services => services.GetRequiredService<GatewayAccess>().ReadAsync(cancellationToken));
        return gateway is { Enabled: true }
            ? new PaymentConfigDto(true, gateway.Provider, gateway.PublicKey, gateway.Environment)
            : new PaymentConfigDto(false, null, null, null);
    }

    public async Task<Result<PayChargeResult>> PayAsync(Guid chargeId, PayChargeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (scope.Kind != DataScopeKind.Tenant || actor.TenantId is not { } tenantId || actor.IsSupportAccess)
        {
            return TenantOnly;
        }

        var token = request.TokenId?.Trim() ?? string.Empty;
        var email = request.Email?.Trim() ?? string.Empty;
        if (!TokenPattern().IsMatch(token))
        {
            return Error.Validation(ErrorCodes.InvalidRequest, "Token inválido", "El pago no trae un token de la pasarela válido: vuelva a abrir el pago.");
        }

        if (email.Length is < 5 or > 50 || !MailAddress.TryCreate(email, out _))
        {
            return Error.Validation(ErrorCodes.InvalidEmail, "Correo inválido", "Ingrese un correo electrónico de hasta 50 caracteres.");
        }

        var runtime = await platform.RunAsync("subscriptions: payment gateway", services => services.GetRequiredService<GatewayAccess>().ReadAsync(cancellationToken));
        if (runtime is not { Enabled: true } || gateways.FirstOrDefault(g => g.Provider == runtime.Provider) is not { } gateway)
        {
            return Unavailable;
        }

        if (runtime.Provider == PaymentGatewayProvider.Culqi && string.IsNullOrEmpty(runtime.SecretKey))
        {
            return Unavailable;
        }

        // 1. The attempt is written, with the charge locked, before the gateway is asked.
        var now = clock.GetUtcNow();
        PaymentAttempt attempt;
        Charge charge;
        decimal balance;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var key = $"charge:{chargeId:N}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
            var found = await db.Charges.AsNoTracking().SingleOrDefaultAsync(c => c.Id == chargeId, cancellationToken);
            if (found is null)
            {
                return Error.NotFound(ErrorCodes.ChargeNotFound, "Cargo no encontrado", "El cargo no existe o no es visible para este contexto.");
            }

            charge = found;
            var paid = await db.Payments.Where(p => p.ChargeId == chargeId).SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;
            balance = charge.TotalAmount - paid;
            if (charge.IsVoid || balance <= 0m)
            {
                return Error.Conflict(ErrorCodes.NothingToPay, "Nada por pagar", "El cargo está pagado o anulado: no tiene saldo.");
            }

            var cents = balance * 100m;
            if (cents != decimal.Truncate(cents) || cents < MinCents || cents > MaxCents)
            {
                return Error.Validation(ErrorCodes.PaymentAmountOutOfRange, "Monto fuera del rango", "La pasarela cobra de S/ 1.00 a S/ 99,999.00. Pague este saldo por transferencia.");
            }

            if (await db.PaymentAttempts.AnyAsync(a => a.ChargeId == chargeId && (a.State == PaymentAttemptState.UnderReview || a.State == PaymentAttemptState.Started), cancellationToken))
            {
                return Error.Conflict(ErrorCodes.PaymentInProgress, "Hay un pago en curso", "Este cargo tiene un pago en curso o en revisión: espere su resultado antes de pagar de nuevo, para no cobrarle dos veces. Si pasaron más de diez minutos, avise a quien le da el servicio.");
            }

            attempt = PaymentAttempt.Start(tenantId.Value, chargeId, balance, actor.UserId, now);
            db.PaymentAttempts.Add(attempt);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // 2. The gateway is asked, with no transaction open. Whatever it does, the attempt is completed.
        GatewayOutcome outcome;
        try
        {
            outcome = await gateway.ChargeAsync(
                new GatewayCharge(
                    runtime.SecretKey ?? string.Empty, token, (long)(balance * 100m), "PEN", email, $"SecureFact {LimaCalendar.PeriodName(charge.Period)}",
                    new Dictionary<string, string> { ["attempt"] = attempt.Id.ToString("N"), ["charge"] = chargeId.ToString("N"), ["tenant"] = tenantId.Value.ToString("N") }),
                cancellationToken);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogGatewayFault(logger, attempt.Id, failure.GetType().Name);
            outcome = new GatewayOutcome(GatewayStatus.Unknown, null, "gateway-fault", "No se sabe si el pago se hizo.");
        }

        // 3. The result is recorded. A payment is recorded only when the gateway says it was made.
        var completedAt = clock.GetUtcNow();
        PayChargeResult result;
        switch (outcome.Status)
        {
            case GatewayStatus.Paid:
                var recorded = await platform.RunAsync("subscriptions: payment from the gateway", services => services.GetRequiredService<ICollections>().RecordPaymentAsync(
                    chargeId, new RecordPaymentRequest(balance, PaymentMethod.Card, LimaCalendar.Today(completedAt), outcome.GatewayChargeId, "Pago en línea con tarjeta"), CancellationToken.None));
                if (recorded.IsSuccess)
                {
                    attempt.Complete(PaymentAttemptState.Paid, outcome.GatewayChargeId, null, null, recorded.Value.Id, completedAt);
                    result = new PayChargeResult(PayStatus.Paid, "Pago recibido. Gracias.", recorded.Value);
                }
                else
                {
                    // The card was charged and the charge changed in between (paid by transfer, voided): the platform reviews it and the person is not asked to pay again.
                    LogUnapplied(logger, attempt.Id, recorded.Error.Code);
                    attempt.Complete(PaymentAttemptState.UnderReview, outcome.GatewayChargeId, "unapplied", $"El pago se hizo pero no pudo aplicarse al cargo ({recorded.Error.Code}).", null, completedAt);
                    result = new PayChargeResult(PayStatus.UnderReview, "Su pago se recibió, pero no pudo aplicarse al cargo. Quien le da el servicio lo revisará: no vuelva a pagar.", null);
                }

                break;
            case GatewayStatus.Declined:
                attempt.Complete(PaymentAttemptState.Declined, outcome.GatewayChargeId, outcome.Code, outcome.Message, null, completedAt);
                result = new PayChargeResult(PayStatus.Declined, outcome.Message ?? "El banco rechazó el pago.", null);
                break;
            case GatewayStatus.NeedsAuthentication:
                attempt.Complete(PaymentAttemptState.NeedsAuthentication, null, outcome.Code, outcome.Message, null, completedAt);
                result = new PayChargeResult(PayStatus.NeedsAuthentication, "Su banco pide una autenticación adicional (3D Secure) que esta pantalla todavía no admite. Pague con otra tarjeta o por transferencia.", null);
                break;
            case GatewayStatus.Failed:
                attempt.Complete(PaymentAttemptState.Failed, null, outcome.Code, outcome.Message, null, completedAt);
                result = new PayChargeResult(PayStatus.Failed, outcome.Message ?? "No se pudo hacer el pago.", null);
                break;
            default:
                attempt.Complete(PaymentAttemptState.UnderReview, outcome.GatewayChargeId, outcome.Code, outcome.Message, null, completedAt);
                result = new PayChargeResult(PayStatus.UnderReview, "No sabemos si su pago se hizo. Quien le da el servicio lo revisará: no vuelva a pagar.", null);
                break;
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.PaymentAttempted, "payment_attempt", attempt.Id.ToString("D"), tenantId.Value,
                NewValues: new Dictionary<string, object?> { ["charge"] = chargeId, ["amount"] = balance, ["state"] = attempt.State.ToString(), ["gatewayChargeId"] = attempt.GatewayChargeId, ["code"] = attempt.Code }),
            cancellationToken);
        return result;
    }

    [GeneratedRegex("^tkn_(test|live)_[A-Za-z0-9]{16}$")]
    private static partial Regex TokenPattern();

    [LoggerMessage(Level = LogLevel.Error, Message = "Payment attempt {AttemptId}: the gateway failed ({Failure}); the attempt is under review.")]
    private static partial void LogGatewayFault(ILogger logger, Guid attemptId, string failure);

    [LoggerMessage(Level = LogLevel.Error, Message = "Payment attempt {AttemptId}: the card was charged but the payment could not be recorded ({Code}); the attempt is under review.")]
    private static partial void LogUnapplied(ILogger logger, Guid attemptId, string code);
}
