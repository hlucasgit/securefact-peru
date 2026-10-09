namespace SecureFact.Subscriptions.Domain;

/// <summary>One version of the price of a plan. Versions are only added, never edited, so a tenant that keeps an old one is never touched by a new one.</summary>
internal sealed class PlanPrice
{
    private PlanPrice()
    {
    }

    public Guid Id { get; private set; }

    public Guid PlanId { get; private set; }

    public int Version { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    public decimal MonthlyFee { get; private set; }

    public int? IncludedDocuments { get; private set; }

    public decimal? OverageUnitPrice { get; private set; }

    public string? Note { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static PlanPrice Create(Guid planId, int version, DateOnly effectiveFrom, decimal monthlyFee, int? includedDocuments, decimal? overageUnitPrice, string? note, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        PlanId = planId,
        Version = version,
        EffectiveFrom = effectiveFrom,
        MonthlyFee = monthlyFee,
        IncludedDocuments = includedDocuments,
        OverageUnitPrice = overageUnitPrice,
        Note = note,
        CreatedAt = now,
    };
}
