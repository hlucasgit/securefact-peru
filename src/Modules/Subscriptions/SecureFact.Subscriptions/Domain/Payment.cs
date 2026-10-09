using SecureFact.Platform.Persistence;
using SecureFact.Subscriptions.Contracts;

namespace SecureFact.Subscriptions.Domain;

/// <summary>Money received against a charge. Only added: a mistake is cancelled with a payment of the opposite amount that points at the first one.</summary>
internal sealed class Payment : ITenantOwned
{
    private Payment()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ChargeId { get; private set; }

    public decimal Amount { get; private set; }

    public PaymentMethod Method { get; private set; }

    public string? Reference { get; private set; }

    public DateOnly PaidOn { get; private set; }

    public string? Note { get; private set; }

    public Guid? ReversesPaymentId { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    public static Payment Create(Guid tenantId, Guid chargeId, decimal amount, PaymentMethod method, string? reference, DateOnly paidOn, string? note, Guid? reverses, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ChargeId = chargeId,
        Amount = amount,
        Method = method,
        Reference = reference,
        PaidOn = paidOn,
        Note = note,
        ReversesPaymentId = reverses,
        RecordedAt = now,
    };
}
