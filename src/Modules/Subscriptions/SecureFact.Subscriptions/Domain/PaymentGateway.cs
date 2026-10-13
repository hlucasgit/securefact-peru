using SecureFact.Platform.Persistence;
using SecureFact.Subscriptions.Contracts;

namespace SecureFact.Subscriptions.Domain;

/// <summary>How the platform takes card payments (ADR-073): one row, for platform staff only. The secret key is stored encrypted and is never returned.</summary>
internal sealed class GatewaySetting
{
    public const int SingleId = 1;

    private GatewaySetting()
    {
    }

    public int Id { get; private set; } = SingleId;

    public PaymentGatewayProvider Provider { get; private set; }

    public string? PublicKey { get; private set; }

    public byte[]? ProtectedSecretKey { get; private set; }

    public bool Enabled { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    public static GatewaySetting Create(PaymentGatewayProvider provider, string? publicKey, byte[]? protectedSecretKey, bool enabled, DateTimeOffset now)
    {
        var setting = new GatewaySetting();
        setting.Apply(provider, publicKey, protectedSecretKey, enabled, now);
        return setting;
    }

    public void Apply(PaymentGatewayProvider provider, string? publicKey, byte[]? protectedSecretKey, bool enabled, DateTimeOffset now)
    {
        Provider = provider;
        PublicKey = publicKey;
        ProtectedSecretKey = protectedSecretKey;
        Enabled = enabled;
        UpdatedAt = now;
    }
}

/// <summary>
/// One try to pay a charge with a card. It is written before the gateway is asked and completed after, so an answer that never arrives leaves a row that says so (ADR-073). It is never deleted;
/// only its outcome is filled in, once.
/// </summary>
internal sealed class PaymentAttempt : ITenantOwned
{
    private PaymentAttempt()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ChargeId { get; private set; }

    public decimal Amount { get; private set; }

    public Guid? UserId { get; private set; }

    public PaymentAttemptState State { get; private set; }

    public string? GatewayChargeId { get; private set; }

    public string? Code { get; private set; }

    public string? Message { get; private set; }

    public Guid? PaymentId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? Resolution { get; private set; }

    public uint Version { get; private set; }

    public static PaymentAttempt Start(Guid tenantId, Guid chargeId, decimal amount, Guid? userId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ChargeId = chargeId,
        Amount = amount,
        UserId = userId,
        State = PaymentAttemptState.Started,
        CreatedAt = now,
    };

    public void Complete(PaymentAttemptState state, string? gatewayChargeId, string? code, string? message, Guid? paymentId, DateTimeOffset now)
    {
        if (State != PaymentAttemptState.Started)
        {
            throw new InvalidOperationException("An attempt is completed once.");
        }

        State = state;
        GatewayChargeId = gatewayChargeId;
        Code = Truncate(code, 60);
        Message = Truncate(message, 300);
        PaymentId = paymentId;
        CompletedAt = now;
    }

    public void Resolve(string note, DateTimeOffset now)
    {
        if (State is PaymentAttemptState.Resolved or PaymentAttemptState.Paid)
        {
            return;
        }

        State = PaymentAttemptState.Resolved;
        Resolution = Truncate(note, 300);
        CompletedAt ??= now;
    }

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}
