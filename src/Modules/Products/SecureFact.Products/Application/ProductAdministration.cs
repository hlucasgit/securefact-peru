using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Catalogs.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.Products.Contracts;
using SecureFact.Products.Domain;
using SecureFact.Products.Infrastructure;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Products.Application;

internal sealed partial class ProductAdministration(
    ProductsDbContext db,
    IDataScope scope,
    ICatalogReader catalogs,
    TimeProvider clock,
    IAuditTrail audit) : IProductAdministration
{
    private const int MaxPage = 200;

    [GeneratedRegex("^[A-Za-z0-9._/-]{1,50}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[A-Za-z0-9]{1,3}$")]
    private static partial Regex UnitPattern();

    [GeneratedRegex(@"^\d{8}$")]
    private static partial Regex SunatProductPattern();

    private static readonly Error Missing = Error.NotFound(ErrorCodes.ProductNotFound, "Producto no encontrado", "El producto no existe o no es visible para este contexto.");

    public async Task<Result<ProductDto>> CreateAsync(ProductDetails details, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Tenant || scope.Current is not { } tenant)
        {
            return Error.Forbidden(ErrorCodes.TenantNotResolved, "Tenant requerido", "Esta operación requiere un contexto de tenant.");
        }

        if (await Validate(details, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var code = details.InternalCode.Trim();
        if (await db.Products.AnyAsync(p => p.InternalCode == code, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.ProductAlreadyExists, "Producto existente", "Ya existe un producto con ese código interno.");
        }

        var product = Product.Create(Guid.CreateVersion7(), tenant.Value, details, clock.GetUtcNow());
        db.Products.Add(product);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.ProductCreated, "product", product.Id.ToString("D"), tenant.Value, NewValues: Values(product)), cancellationToken);
        return ToDto(product);
    }

    public async Task<Result<ProductDto>> GetAsync(Guid productId, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId, cancellationToken);
        return product is null ? Missing : ToDto(product);
    }

    public async Task<IReadOnlyList<ProductDto>> ListAsync(string? search, bool includeInactive, int skip, int take, CancellationToken cancellationToken)
    {
        var query = db.Products.AsNoTracking().AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var pattern = $"%{term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
            query = query.Where(p => EF.Functions.ILike(p.Description, pattern, "\\") || EF.Functions.ILike(p.InternalCode, pattern, "\\"));
        }

        var rows = await query.OrderBy(p => p.Description).ThenBy(p => p.InternalCode)
            .Skip(Math.Max(skip, 0)).Take(Math.Clamp(take, 1, MaxPage)).ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<ProductDto>> UpdateAsync(Guid productId, ProductDetails details, CancellationToken cancellationToken)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (product is null)
        {
            return Missing;
        }

        if (details.InternalCode?.Trim() != product.InternalCode)
        {
            return Error.Validation(ErrorCodes.InvalidProduct, "Código inmutable", "El código interno identifica al producto y no se puede cambiar; cree un producto nuevo.");
        }

        if (await Validate(details, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var before = Values(product);
        product.Apply(details, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.ProductUpdated, "product", product.Id.ToString("D"), product.TenantId, OldValues: before, NewValues: Values(product)), cancellationToken);
        return ToDto(product);
    }

    public async Task<Result<Unit>> DeactivateAsync(Guid productId, CancellationToken cancellationToken)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (product is null)
        {
            return Missing;
        }

        product.Deactivate(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.ProductDeactivated, "product", product.Id.ToString("D"), product.TenantId), cancellationToken);
        return Unit.Value;
    }

    private async Task<Error?> Validate(ProductDetails d, CancellationToken cancellationToken)
    {
        static Error Bad(string detail) => Error.Validation(ErrorCodes.InvalidProduct, "Datos de producto inválidos", detail);

        if (d is null || !CodePattern().IsMatch(d.InternalCode?.Trim() ?? string.Empty))
        {
            return Bad("El código interno es obligatorio (hasta 50 caracteres: letras, dígitos, '.', '_', '/', '-').");
        }

        if (string.IsNullOrWhiteSpace(d.Description) || d.Description.Trim().Length > 500)
        {
            return Bad("La descripción es obligatoria (máximo 500 caracteres).");
        }

        if (!Enum.IsDefined(d.Kind))
        {
            return Bad("El tipo de producto debe ser Goods o Service.");
        }

        if (!UnitPattern().IsMatch(d.UnitCode?.Trim() ?? string.Empty))
        {
            return Bad("La unidad de medida debe tener hasta 3 caracteres alfanuméricos (UN/ECE, p. ej. NIU, ZZ, KGM).");
        }

        if (d.UnitValue < 0 || decimal.Round(d.UnitValue, 10) != d.UnitValue)
        {
            return Bad("El valor unitario debe ser no negativo y tener hasta 10 decimales.");
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (!await catalogs.IsValidCodeAsync("07", d.IgvAffectationCode?.Trim() ?? string.Empty, today, cancellationToken))
        {
            return Bad("El tipo de afectación del IGV no existe en el catálogo 07.");
        }

        if (d.SunatProductCode is { Length: > 0 } sunat && !SunatProductPattern().IsMatch(sunat.Trim()))
        {
            return Bad("El código de producto SUNAT debe tener 8 dígitos.");
        }

        return d.Category is { Length: > 100 } ? Bad("La categoría admite máximo 100 caracteres.") : null;
    }

    private static Dictionary<string, object?> Values(Product p) => new()
    {
        ["internalCode"] = p.InternalCode,
        ["description"] = p.Description,
        ["kind"] = p.Kind,
        ["unitCode"] = p.UnitCode,
        ["unitValue"] = p.UnitValue,
        ["igvAffectationCode"] = p.IgvAffectationCode,
        ["sunatProductCode"] = p.SunatProductCode,
        ["category"] = p.Category,
    };

    private static ProductDto ToDto(Product p) => new(
        p.Id, p.TenantId, p.InternalCode, p.Description, p.Kind, p.UnitCode, p.UnitValue, p.IgvAffectationCode,
        p.SunatProductCode, p.Category, p.IsActive, p.CreatedAt);
}
