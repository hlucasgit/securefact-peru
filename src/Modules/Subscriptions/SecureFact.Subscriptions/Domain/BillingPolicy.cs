namespace SecureFact.Subscriptions.Domain;

/// <summary>When a charge falls due and when an unpaid one suspends the account. A version applies to the charges issued from its date on; the charges already issued keep their dates.</summary>
internal sealed class BillingPolicy
{
    private BillingPolicy()
    {
    }

    public Guid Id { get; private set; }

    public int Version { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    public int DueDays { get; private set; }

    public int? SuspendAfterDays { get; private set; }

    public string? Note { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static BillingPolicy Create(int version, DateOnly effectiveFrom, int dueDays, int? suspendAfterDays, string? note, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        Version = version,
        EffectiveFrom = effectiveFrom,
        DueDays = dueDays,
        SuspendAfterDays = suspendAfterDays,
        Note = note,
        CreatedAt = now,
    };
}
