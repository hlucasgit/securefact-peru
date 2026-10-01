using SecureFact.SharedKernel.Results;

namespace SecureFact.Products.Contracts;

public enum ProductKind
{
    Goods,
    Service,
}

/// <param name="InternalCode">Tenant's own code; unique per tenant and immutable.</param>
/// <param name="UnitCode">Unit of measure code (UN/ECE, e.g. NIU, ZZ, KGM), at most 3 characters.</param>
/// <param name="UnitValue">Value without taxes, non-negative, at most 10 decimals.</param>
/// <param name="IgvAffectationCode">SUNAT catalogue No. 07 code.</param>
/// <param name="SunatProductCode">Optional 8-digit SUNAT product code (UNSPSC). Not assumed mandatory: it is only required for specific scenarios.</param>
public sealed record ProductDetails(
    string InternalCode,
    string Description,
    ProductKind Kind,
    string UnitCode,
    decimal UnitValue,
    string IgvAffectationCode,
    string? SunatProductCode = null,
    string? Category = null);

public sealed record ProductDto(
    Guid Id,
    Guid TenantId,
    string InternalCode,
    string Description,
    ProductKind Kind,
    string UnitCode,
    decimal UnitValue,
    string IgvAffectationCode,
    string? SunatProductCode,
    string? Category,
    bool IsActive,
    DateTimeOffset CreatedAt);

public interface IProductAdministration
{
    Task<Result<ProductDto>> CreateAsync(ProductDetails details, CancellationToken cancellationToken);

    Task<Result<ProductDto>> GetAsync(Guid productId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductDto>> ListAsync(string? search, bool includeInactive, int skip, int take, CancellationToken cancellationToken);

    /// <summary>The internal code is the product's identity and cannot change.</summary>
    Task<Result<ProductDto>> UpdateAsync(Guid productId, ProductDetails details, CancellationToken cancellationToken);

    Task<Result<Unit>> DeactivateAsync(Guid productId, CancellationToken cancellationToken);
}
