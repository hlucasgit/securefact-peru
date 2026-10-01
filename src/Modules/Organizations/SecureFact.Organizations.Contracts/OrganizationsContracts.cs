using SecureFact.SharedKernel.Results;

namespace SecureFact.Organizations.Contracts;

public enum CompanyStatus
{
    Active,
    Inactive,
}

/// <summary>Editable company data. The RUC is fixed at creation: a different RUC is a different company.</summary>
public sealed record CompanyDetails(
    string LegalName,
    string? TradeName,
    string FiscalAddress,
    string Ubigeo,
    string? TaxRegime,
    string? ContactEmail,
    string TimeZone = "America/Lima",
    string DefaultCurrency = "PEN");

public sealed record CreateCompanyRequest(string Ruc, CompanyDetails Details);

public sealed record CompanyDto(
    Guid Id,
    Guid TenantId,
    string Ruc,
    string LegalName,
    string? TradeName,
    string FiscalAddress,
    string Ubigeo,
    string? TaxRegime,
    string? ContactEmail,
    string TimeZone,
    string DefaultCurrency,
    CompanyStatus Status,
    DateTimeOffset CreatedAt);

public sealed record EstablishmentDetails(string Name, string Address, string Ubigeo);

public sealed record CreateEstablishmentRequest(string Code, EstablishmentDetails Details);

public sealed record EstablishmentDto(
    Guid Id,
    Guid CompanyId,
    string Code,
    string Name,
    string Address,
    string Ubigeo,
    bool IsActive,
    DateTimeOffset CreatedAt);

public interface ICompanyAdministration
{
    Task<Result<CompanyDto>> CreateAsync(CreateCompanyRequest request, CancellationToken cancellationToken);

    Task<Result<CompanyDto>> GetAsync(Guid companyId, CancellationToken cancellationToken);

    Task<IReadOnlyList<CompanyDto>> ListAsync(int skip, int take, CancellationToken cancellationToken);

    Task<Result<CompanyDto>> UpdateAsync(Guid companyId, CompanyDetails details, CancellationToken cancellationToken);

    /// <summary>Companies are never deleted: fiscal history must stay traceable. Deactivation blocks new activity only.</summary>
    Task<Result<Unit>> DeactivateAsync(Guid companyId, CancellationToken cancellationToken);

    Task<Result<EstablishmentDto>> AddEstablishmentAsync(Guid companyId, CreateEstablishmentRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<EstablishmentDto>> ListEstablishmentsAsync(Guid companyId, CancellationToken cancellationToken);

    Task<Result<EstablishmentDto>> UpdateEstablishmentAsync(Guid companyId, Guid establishmentId, EstablishmentDetails details, CancellationToken cancellationToken);

    Task<Result<Unit>> DeactivateEstablishmentAsync(Guid companyId, Guid establishmentId, CancellationToken cancellationToken);
}
