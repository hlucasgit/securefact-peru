using SecureFact.SharedKernel.Results;

namespace SecureFact.Subscriptions.Contracts;

/// <summary>Who takes the card payments of the charges (ADR-073). <see cref="Sandbox"/> is a simulator for development and tests, and the platform refuses it unless it is allowed by configuration.</summary>
public enum PaymentGatewayProvider
{
    Culqi = 1,
    Sandbox = 2,
}

/// <param name="PublicKey">The public key of the account of the gateway (<c>pk_test_…</c> or <c>pk_live_…</c> for Culqi): it is the one that the browser uses.</param>
/// <param name="SecretKey">The secret key (<c>sk_…</c>). Null keeps the one that is stored; it is never returned.</param>
public sealed record GatewaySettingsInput(PaymentGatewayProvider Provider, string? PublicKey, string? SecretKey, bool Enabled);

/// <param name="Environment"><c>test</c> or <c>live</c> by the prefix of the keys (<c>sandbox</c> for the simulator).</param>
public sealed record GatewaySettingsDto(PaymentGatewayProvider Provider, string? PublicKey, bool HasSecretKey, string Environment, bool Enabled, DateTimeOffset UpdatedAt);

/// <summary>What the screen of a tenant needs to offer paying a charge online: whether it can, with what, and the public key.</summary>
public sealed record PaymentConfigDto(bool Enabled, PaymentGatewayProvider? Provider, string? PublicKey, string? Environment);

/// <param name="TokenId">The token that the checkout of the gateway returned for the card (<c>tkn_…</c>): the card never reaches this platform.</param>
/// <param name="Email">The e-mail of the person who pays, which the gateway asks for.</param>
public sealed record PayChargeRequest(string TokenId, string Email);

public enum PayStatus
{
    /// <summary>The card was charged and the payment is recorded in the charge.</summary>
    Paid,

    /// <summary>The bank refused the payment.</summary>
    Declined,

    /// <summary>The bank asks for an extra authentication (3D Secure), which this version does not support.</summary>
    NeedsAuthentication,

    /// <summary>The payment was not made (the gateway refused the request or is not available).</summary>
    Failed,

    /// <summary>The platform cannot tell whether the card was charged (the gateway did not answer) or a payment that was made could not be applied: the platform reviews it, and the person is not to pay again.</summary>
    UnderReview,
}

public sealed record PayChargeResult(PayStatus Status, string Message, PaymentDto? Payment);

public enum PaymentAttemptState
{
    Started,
    Paid,
    Declined,
    NeedsAuthentication,
    Failed,
    UnderReview,
    Resolved,
}

public sealed record PaymentAttemptDto(
    Guid Id, Guid TenantId, string TenantName, Guid ChargeId, string Period, decimal Amount, PaymentAttemptState State, string? GatewayChargeId, string? Code, string? Message, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
    string? Resolution);

/// <summary>The gateway of card payments, for the platform (ADR-073): the keys and whether it is on. Platform staff only; the secret key is never read back.</summary>
public interface IGatewaySettings
{
    Task<Result<GatewaySettingsDto>> GetAsync(CancellationToken cancellationToken);

    Task<Result<GatewaySettingsDto>> SetAsync(GatewaySettingsInput input, CancellationToken cancellationToken);

    /// <summary>The attempts that need a person: the ones whose outcome is not known and the payments made that could not be applied, and the ones that were left half way.</summary>
    Task<Result<IReadOnlyList<PaymentAttemptDto>>> ListUnresolvedAsync(CancellationToken cancellationToken);

    /// <summary>The platform closes an attempt after reviewing it with the gateway and, if the money came, recording the payment by hand.</summary>
    Task<Result<PaymentAttemptDto>> ResolveAsync(Guid attemptId, string note, CancellationToken cancellationToken);
}

/// <summary>Paying a charge with a card, by a person of the account that owes it (ADR-073).</summary>
public interface IChargePayments
{
    Task<Result<PaymentConfigDto>> GetConfigAsync(CancellationToken cancellationToken);

    /// <summary>Charges the card of the token for what is owed of the charge and, if the bank accepts, records the payment. One attempt at a time per charge.</summary>
    Task<Result<PayChargeResult>> PayAsync(Guid chargeId, PayChargeRequest request, CancellationToken cancellationToken);
}
