using SecureFact.Organizations.Contracts;
using SecureFact.Platform.Persistence;

namespace SecureFact.Organizations.Domain;

internal sealed class Company : ITenantOwned
{
    private readonly List<Establishment> _establishments = [];

    private Company()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Ruc { get; private set; } = string.Empty;

    public string LegalName { get; private set; } = string.Empty;

    public string? TradeName { get; private set; }

    public string FiscalAddress { get; private set; } = string.Empty;

    public string Ubigeo { get; private set; } = string.Empty;

    public string? TaxRegime { get; private set; }

    public string? ContactEmail { get; private set; }

    public string TimeZone { get; private set; } = string.Empty;

    public string DefaultCurrency { get; private set; } = string.Empty;

    public CompanyStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    public IReadOnlyCollection<Establishment> Establishments => _establishments;

    public static Company Create(Guid id, Guid tenantId, string ruc, CompanyDetails details, DateTimeOffset now)
    {
        var company = new Company { Id = id, TenantId = tenantId, Ruc = ruc, Status = CompanyStatus.Active, CreatedAt = now };
        company.Apply(details, now);
        return company;
    }

    public void Apply(CompanyDetails details, DateTimeOffset now)
    {
        LegalName = details.LegalName.Trim();
        TradeName = string.IsNullOrWhiteSpace(details.TradeName) ? null : details.TradeName.Trim();
        FiscalAddress = details.FiscalAddress.Trim();
        Ubigeo = details.Ubigeo.Trim();
        TaxRegime = string.IsNullOrWhiteSpace(details.TaxRegime) ? null : details.TaxRegime.Trim();
        ContactEmail = string.IsNullOrWhiteSpace(details.ContactEmail) ? null : details.ContactEmail.Trim();
        TimeZone = details.TimeZone.Trim();
        DefaultCurrency = details.DefaultCurrency.Trim().ToUpperInvariant();
        UpdatedAt = now;
    }

    public void Deactivate(DateTimeOffset now)
    {
        Status = CompanyStatus.Inactive;
        UpdatedAt = now;
    }
}

internal sealed class Establishment : ITenantOwned
{
    private Establishment()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string Address { get; private set; } = string.Empty;

    public string Ubigeo { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    public static Establishment Create(Guid id, Guid tenantId, Guid companyId, string code, EstablishmentDetails details, DateTimeOffset now)
    {
        var establishment = new Establishment { Id = id, TenantId = tenantId, CompanyId = companyId, Code = code, IsActive = true, CreatedAt = now };
        establishment.Apply(details, now);
        return establishment;
    }

    public void Apply(EstablishmentDetails details, DateTimeOffset now)
    {
        Name = details.Name.Trim();
        Address = details.Address.Trim();
        Ubigeo = details.Ubigeo.Trim();
        UpdatedAt = now;
    }

    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        UpdatedAt = now;
    }
}
