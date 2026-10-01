using SecureFact.Customers.Contracts;
using SecureFact.Platform.Persistence;

namespace SecureFact.Customers.Domain;

internal sealed class Customer : ITenantOwned
{
    private Customer()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string DocumentTypeCode { get; private set; } = string.Empty;

    public string DocumentNumber { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? Address { get; private set; }

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    public static Customer Create(Guid id, Guid tenantId, CustomerDetails details, DateTimeOffset now)
    {
        var customer = new Customer
        {
            Id = id,
            TenantId = tenantId,
            DocumentTypeCode = details.DocumentTypeCode.Trim(),
            DocumentNumber = Normalize(details.DocumentNumber),
            IsActive = true,
            CreatedAt = now,
        };
        customer.Apply(details, now);
        return customer;
    }

    public void Apply(CustomerDetails details, DateTimeOffset now)
    {
        Name = details.Name.Trim();
        Address = Blank(details.Address);
        Email = Blank(details.Email);
        Phone = Blank(details.Phone);
        UpdatedAt = now;
    }

    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        UpdatedAt = now;
    }

    public static string Normalize(string number) => number.Trim().ToUpperInvariant();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
