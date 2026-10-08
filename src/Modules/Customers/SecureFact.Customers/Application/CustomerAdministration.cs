using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Catalogs.Contracts;
using SecureFact.Customers.Contracts;
using SecureFact.Customers.Domain;
using SecureFact.Customers.Infrastructure;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Customers.Application;

internal sealed partial class CustomerAdministration(
    CustomersDbContext db,
    IDataScope scope,
    ICatalogReader catalogs,
    TimeProvider clock,
    IAuditTrail audit) : ICustomerAdministration
{
    private const int MaxPage = 200;

    private readonly Dictionary<string, bool> _documentTypes = [];

    private static readonly Error Missing = Error.NotFound(ErrorCodes.CustomerNotFound, "Cliente no encontrado", "El cliente no existe o no es visible para este contexto.");

    public async Task<Result<CustomerDto>> CreateAsync(CustomerDetails details, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Tenant || scope.Current is not { } tenant)
        {
            return Error.Forbidden(ErrorCodes.TenantNotResolved, "Tenant requerido", "Esta operación requiere un contexto de tenant.");
        }

        if (await Validate(details, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var customer = Customer.Create(Guid.CreateVersion7(), tenant.Value, details, clock.GetUtcNow());
        if (await db.Customers.AnyAsync(c => c.DocumentTypeCode == customer.DocumentTypeCode && c.DocumentNumber == customer.DocumentNumber, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.CustomerAlreadyExists, "Cliente existente", "Ya existe un cliente con ese documento de identidad.");
        }

        db.Customers.Add(customer);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.CustomerCreated, "customer", customer.Id.ToString("D"), tenant.Value, NewValues: Values(customer)), cancellationToken);
        return ToDto(customer);
    }

    public async Task<Result<CustomerDto>> GetAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == customerId, cancellationToken);
        return customer is null ? Missing : ToDto(customer);
    }

    public async Task<IReadOnlyList<CustomerDto>> ListAsync(string? search, bool includeInactive, int skip, int take, CancellationToken cancellationToken)
    {
        var query = db.Customers.AsNoTracking().AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var upper = term.ToUpperInvariant();
            query = query.Where(c => EF.Functions.ILike(c.Name, $"%{EscapeLike(term)}%", "\\") || c.DocumentNumber.StartsWith(upper));
        }

        var rows = await query.OrderBy(c => c.Name).ThenBy(c => c.DocumentNumber)
            .Skip(Math.Max(skip, 0)).Take(Math.Clamp(take, 1, MaxPage)).ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<CustomerDto>> UpdateAsync(Guid customerId, CustomerDetails details, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == customerId, cancellationToken);
        if (customer is null)
        {
            return Missing;
        }

        if (details.DocumentTypeCode?.Trim() != customer.DocumentTypeCode || Customer.Normalize(details.DocumentNumber ?? string.Empty) != customer.DocumentNumber)
        {
            return Error.Validation(ErrorCodes.InvalidCustomer, "Documento inmutable", "El tipo y número de documento identifican al cliente y no se pueden cambiar; cree un cliente nuevo.");
        }

        if (await Validate(details, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var before = Values(customer);
        customer.Apply(details, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.CustomerUpdated, "customer", customer.Id.ToString("D"), customer.TenantId, OldValues: before, NewValues: Values(customer)), cancellationToken);
        return ToDto(customer);
    }

    public async Task<Result<Unit>> DeactivateAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == customerId, cancellationToken);
        if (customer is null)
        {
            return Missing;
        }

        customer.Deactivate(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.CustomerDeactivated, "customer", customer.Id.ToString("D"), customer.TenantId), cancellationToken);
        return Unit.Value;
    }

    private async Task<Error?> Validate(CustomerDetails details, CancellationToken cancellationToken)
    {
        static Error Bad(string detail) => Error.Validation(ErrorCodes.InvalidCustomer, "Datos de cliente inválidos", detail);

        if (details is null || string.IsNullOrWhiteSpace(details.Name) || details.Name.Trim().Length > 250)
        {
            return Bad("El nombre o razón social es obligatorio (máximo 250 caracteres).");
        }

        if (IdentityDocuments.Validate(details.DocumentTypeCode, details.DocumentNumber) is { } problem)
        {
            return Bad(problem);
        }

        if (!await IsKnownDocumentTypeAsync(details.DocumentTypeCode.Trim(), cancellationToken))
        {
            return Bad($"El tipo de documento '{details.DocumentTypeCode}' no está vigente en el catálogo 06.");
        }

        if (details.Address is { Length: > 250 } || details.Phone is { Length: > 30 })
        {
            return Bad("La dirección o el teléfono exceden la longitud permitida.");
        }

        if (!string.IsNullOrWhiteSpace(details.Email)
            && (details.Email.Trim().Length > 254 || !MailAddress.TryCreate(details.Email.Trim(), out var parsed) || !string.Equals(parsed.Address, details.Email.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return Bad("El correo electrónico no es válido.");
        }

        return null;
    }

    /// <summary>Catalogue 06 lookups, remembered for the life of the request: an import of thousands of rows asks about the same few codes.</summary>
    private async Task<bool> IsKnownDocumentTypeAsync(string code, CancellationToken cancellationToken)
    {
        if (!_documentTypes.TryGetValue(code, out var known))
        {
            known = await catalogs.IsValidCodeAsync("06", code, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), cancellationToken);
            _documentTypes[code] = known;
        }

        return known;
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static Dictionary<string, object?> Values(Customer c) => new()
    {
        ["documentType"] = c.DocumentTypeCode,
        ["documentNumber"] = c.DocumentNumber,
        ["name"] = c.Name,
        ["address"] = c.Address,
        ["email"] = c.Email,
        ["phone"] = c.Phone,
    };

    private static CustomerDto ToDto(Customer c) => new(c.Id, c.TenantId, c.DocumentTypeCode, c.DocumentNumber, c.Name, c.Address, c.Email, c.Phone, c.IsActive, c.CreatedAt);
}
