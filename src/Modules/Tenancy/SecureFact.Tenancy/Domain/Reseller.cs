namespace SecureFact.Tenancy.Domain;

/// <summary>A reseller: a company that brings tenants to the platform and manages them. It belongs to the platform, not to a tenant.</summary>
internal sealed class Reseller
{
    private Reseller()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public uint Version { get; private set; }

    public static Reseller Create(Guid id, string name, DateTimeOffset now) => new() { Id = id, Name = name, IsActive = true, CreatedAt = now };

    public void Update(string name, bool isActive)
    {
        Name = name;
        IsActive = isActive;
    }
}
