using SecureFact.SharedKernel.Results;

namespace SecureFact.Customers.Contracts;

/// <param name="DocumentTypeCode">SUNAT catalogue No. 06 code (6 RUC, 1 DNI, 4 CE, 7 passport, A diplomatic id, 0 none).</param>
public sealed record CustomerDetails(string DocumentTypeCode, string DocumentNumber, string Name, string? Address = null, string? Email = null, string? Phone = null);

public sealed record CustomerDto(
    Guid Id,
    Guid TenantId,
    string DocumentTypeCode,
    string DocumentNumber,
    string Name,
    string? Address,
    string? Email,
    string? Phone,
    bool IsActive,
    DateTimeOffset CreatedAt);

public interface ICustomerAdministration
{
    Task<Result<CustomerDto>> CreateAsync(CustomerDetails details, CancellationToken cancellationToken);

    Task<Result<CustomerDto>> GetAsync(Guid customerId, CancellationToken cancellationToken);

    /// <summary>Lists customers ordered by name; <paramref name="search"/> matches name or document number.</summary>
    Task<IReadOnlyList<CustomerDto>> ListAsync(string? search, bool includeInactive, int skip, int take, CancellationToken cancellationToken);

    /// <summary>The document type and number are the customer's identity and cannot change; everything else can.</summary>
    Task<Result<CustomerDto>> UpdateAsync(Guid customerId, CustomerDetails details, CancellationToken cancellationToken);

    Task<Result<Unit>> DeactivateAsync(Guid customerId, CancellationToken cancellationToken);
}
