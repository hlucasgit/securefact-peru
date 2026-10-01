using SecureFact.Platform.Persistence;
using SecureFact.Products.Contracts;

namespace SecureFact.Products.Domain;

internal sealed class Product : ITenantOwned
{
    private Product()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string InternalCode { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public ProductKind Kind { get; private set; }

    public string UnitCode { get; private set; } = string.Empty;

    public decimal UnitValue { get; private set; }

    public string IgvAffectationCode { get; private set; } = string.Empty;

    public string? SunatProductCode { get; private set; }

    public string? Category { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    public static Product Create(Guid id, Guid tenantId, ProductDetails details, DateTimeOffset now)
    {
        var product = new Product { Id = id, TenantId = tenantId, InternalCode = details.InternalCode.Trim(), IsActive = true, CreatedAt = now };
        product.Apply(details, now);
        return product;
    }

    public void Apply(ProductDetails details, DateTimeOffset now)
    {
        Description = details.Description.Trim();
        Kind = details.Kind;
        UnitCode = details.UnitCode.Trim().ToUpperInvariant();
        UnitValue = details.UnitValue;
        IgvAffectationCode = details.IgvAffectationCode.Trim();
        SunatProductCode = string.IsNullOrWhiteSpace(details.SunatProductCode) ? null : details.SunatProductCode.Trim();
        Category = string.IsNullOrWhiteSpace(details.Category) ? null : details.Category.Trim();
        UpdatedAt = now;
    }

    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        UpdatedAt = now;
    }
}
