namespace SecureFact.SharedKernel.Domain;

public readonly record struct TenantId(Guid Value)
{
    public static TenantId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}
