using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Organizations.Contracts;
using SecureFact.Organizations.Domain;
using SecureFact.Organizations.Infrastructure;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.Tenancy.Contracts;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Organizations.Application;

internal sealed partial class CompanyAdministration(OrganizationsDbContext db, IDataScope scope, TimeProvider clock, IAuditTrail audit, IPlanLimits plans)
    : ICompanyAdministration
{
    private const int MaxPage = 200;

    [GeneratedRegex(@"^\d{6}$")]
    private static partial Regex UbigeoPattern();

    // Structural check only; the official establishment code rules are tracked as pending in docs/regulatory/matrix.md (R-023).
    [GeneratedRegex("^[0-9A-Z]{4}$")]
    private static partial Regex EstablishmentCodePattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    private static readonly Error CompanyMissing = Error.NotFound(ErrorCodes.CompanyNotFound, "Empresa no encontrada", "La empresa no existe o no es visible para este contexto.");
    private static readonly Error EstablishmentMissing = Error.NotFound(ErrorCodes.EstablishmentNotFound, "Establecimiento no encontrado", "El establecimiento no existe o no es visible para este contexto.");

    private Guid? TenantGuid => scope.Kind == DataScopeKind.Tenant ? scope.Current?.Value : null;

    public async Task<Result<CompanyDto>> CreateAsync(CreateCompanyRequest request, CancellationToken cancellationToken)
    {
        if (TenantGuid is not { } tenantId)
        {
            return NoTenant();
        }

        var ruc = Ruc.Create(request.Ruc);
        if (!ruc.IsSuccess)
        {
            return ruc.Error;
        }

        if (ValidateCompany(request.Details) is { } invalid)
        {
            return invalid;
        }

        if (await db.Companies.AnyAsync(c => c.Ruc == ruc.Value.Value, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.CompanyAlreadyExists, "Empresa existente", "Ya existe una empresa con ese RUC en esta cuenta.");
        }

        var allowed = await plans.EnsureCanAddAsync(new TenantId(tenantId), PlanResource.Companies, await db.Companies.CountAsync(cancellationToken), cancellationToken);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        var company = Company.Create(Guid.CreateVersion7(), tenantId, ruc.Value.Value, request.Details, clock.GetUtcNow());
        db.Companies.Add(company);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(
            AuditActions.CompanyCreated, "company", company.Id.ToString("D"), tenantId,
            NewValues: CompanyValues(company)), cancellationToken);
        return ToDto(company);
    }

    public async Task<Result<CompanyDto>> GetAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.Id == companyId, cancellationToken);
        return company is null ? CompanyMissing : ToDto(company);
    }

    public async Task<int> CountAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await db.Companies.CountAsync(c => c.TenantId == tenantId, cancellationToken);

    public async Task<IReadOnlyList<CompanyDto>> ListAsync(int skip, int take, CancellationToken cancellationToken)
    {
        var rows = await db.Companies.AsNoTracking()
            .OrderBy(c => c.LegalName).ThenBy(c => c.Ruc)
            .Skip(Math.Max(skip, 0)).Take(Math.Clamp(take, 1, MaxPage))
            .ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<CompanyDto>> UpdateAsync(Guid companyId, CompanyDetails details, CancellationToken cancellationToken)
    {
        if (ValidateCompany(details) is { } invalid)
        {
            return invalid;
        }

        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId, cancellationToken);
        if (company is null)
        {
            return CompanyMissing;
        }

        var before = CompanyValues(company);
        company.Apply(details, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(
            AuditActions.CompanyUpdated, "company", company.Id.ToString("D"), company.TenantId,
            OldValues: before, NewValues: CompanyValues(company)), cancellationToken);
        return ToDto(company);
    }

    public async Task<Result<Unit>> DeactivateAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId, cancellationToken);
        if (company is null)
        {
            return CompanyMissing;
        }

        company.Deactivate(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.CompanyDeactivated, "company", company.Id.ToString("D"), company.TenantId), cancellationToken);
        return Unit.Value;
    }

    public async Task<Result<EstablishmentDto>> AddEstablishmentAsync(Guid companyId, CreateEstablishmentRequest request, CancellationToken cancellationToken)
    {
        var code = request.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!EstablishmentCodePattern().IsMatch(code))
        {
            return Error.Validation(ErrorCodes.InvalidEstablishment, "Código inválido", "El código del establecimiento debe tener 4 caracteres alfanuméricos.");
        }

        if (ValidateEstablishment(request.Details) is { } invalid)
        {
            return invalid;
        }

        var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.Id == companyId, cancellationToken);
        if (company is null)
        {
            return CompanyMissing;
        }

        if (company.Status != CompanyStatus.Active)
        {
            return Error.Validation(ErrorCodes.InvalidEstablishment, "Empresa inactiva", "No se pueden agregar establecimientos a una empresa inactiva.");
        }

        if (await db.Establishments.AnyAsync(e => e.CompanyId == companyId && e.Code == code, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.EstablishmentAlreadyExists, "Establecimiento existente", "Ya existe un establecimiento con ese código en la empresa.");
        }

        var establishment = Establishment.Create(Guid.CreateVersion7(), company.TenantId, companyId, code, request.Details, clock.GetUtcNow());
        db.Establishments.Add(establishment);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(
            AuditActions.EstablishmentCreated, "establishment", establishment.Id.ToString("D"), company.TenantId,
            NewValues: EstablishmentValues(establishment)), cancellationToken);
        return ToDto(establishment);
    }

    public async Task<IReadOnlyList<EstablishmentDto>> ListEstablishmentsAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var rows = await db.Establishments.AsNoTracking().Where(e => e.CompanyId == companyId).OrderBy(e => e.Code).ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<EstablishmentDto>> UpdateEstablishmentAsync(Guid companyId, Guid establishmentId, EstablishmentDetails details, CancellationToken cancellationToken)
    {
        if (ValidateEstablishment(details) is { } invalid)
        {
            return invalid;
        }

        var establishment = await db.Establishments.SingleOrDefaultAsync(e => e.Id == establishmentId && e.CompanyId == companyId, cancellationToken);
        if (establishment is null)
        {
            return EstablishmentMissing;
        }

        var before = EstablishmentValues(establishment);
        establishment.Apply(details, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(
            AuditActions.EstablishmentUpdated, "establishment", establishment.Id.ToString("D"), establishment.TenantId,
            OldValues: before, NewValues: EstablishmentValues(establishment)), cancellationToken);
        return ToDto(establishment);
    }

    public async Task<Result<Unit>> DeactivateEstablishmentAsync(Guid companyId, Guid establishmentId, CancellationToken cancellationToken)
    {
        var establishment = await db.Establishments.SingleOrDefaultAsync(e => e.Id == establishmentId && e.CompanyId == companyId, cancellationToken);
        if (establishment is null)
        {
            return EstablishmentMissing;
        }

        establishment.Deactivate(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.EstablishmentDeactivated, "establishment", establishment.Id.ToString("D"), establishment.TenantId), cancellationToken);
        return Unit.Value;
    }

    private static Error NoTenant() =>
        Error.Forbidden(ErrorCodes.TenantNotResolved, "Tenant requerido", "Esta operación requiere un contexto de tenant.");

    private static Error? ValidateCompany(CompanyDetails d)
    {
        static Error Bad(string detail) => Error.Validation(ErrorCodes.InvalidCompany, "Datos de empresa inválidos", detail);

        if (d is null || string.IsNullOrWhiteSpace(d.LegalName) || d.LegalName.Trim().Length > 250)
        {
            return Bad("La razón social es obligatoria (máximo 250 caracteres).");
        }

        if (d.TradeName is { Length: > 250 })
        {
            return Bad("El nombre comercial admite máximo 250 caracteres.");
        }

        if (string.IsNullOrWhiteSpace(d.FiscalAddress) || d.FiscalAddress.Trim().Length > 250)
        {
            return Bad("El domicilio fiscal es obligatorio (máximo 250 caracteres).");
        }

        if (!UbigeoPattern().IsMatch(d.Ubigeo?.Trim() ?? string.Empty))
        {
            return Bad("El ubigeo debe tener 6 dígitos.");
        }

        if (!string.IsNullOrWhiteSpace(d.ContactEmail) && !IsEmail(d.ContactEmail.Trim()))
        {
            return Bad("El correo de contacto no es válido.");
        }

        if (d.TaxRegime is { Length: > 60 })
        {
            return Bad("El régimen admite máximo 60 caracteres.");
        }

        if (!string.IsNullOrWhiteSpace(d.DetractionAccount) && !DetractionAccountPattern().IsMatch(d.DetractionAccount.Trim()))
        {
            return Bad("La cuenta de detracciones debe ser alfanumérica (con guiones) de hasta 100 caracteres.");
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(d.TimeZone?.Trim() ?? string.Empty, out _))
        {
            return Bad("La zona horaria no es válida.");
        }

        if (!CurrencyPattern().IsMatch(d.DefaultCurrency?.Trim().ToUpperInvariant() ?? string.Empty))
        {
            return Bad("La moneda debe ser un código de 3 letras.");
        }

        return null;
    }

    private static Error? ValidateEstablishment(EstablishmentDetails d)
    {
        static Error Bad(string detail) => Error.Validation(ErrorCodes.InvalidEstablishment, "Datos de establecimiento inválidos", detail);

        if (d is null || string.IsNullOrWhiteSpace(d.Name) || d.Name.Trim().Length > 250)
        {
            return Bad("El nombre es obligatorio (máximo 250 caracteres).");
        }

        if (string.IsNullOrWhiteSpace(d.Address) || d.Address.Trim().Length > 250)
        {
            return Bad("La dirección es obligatoria (máximo 250 caracteres).");
        }

        return UbigeoPattern().IsMatch(d.Ubigeo?.Trim() ?? string.Empty) ? null : Bad("El ubigeo debe tener 6 dígitos.");
    }

    [GeneratedRegex("^[0-9A-Za-z-]{1,100}$")]
    private static partial Regex DetractionAccountPattern();

    private static bool IsEmail(string value) =>
        value.Length <= 254 && MailAddress.TryCreate(value, out var parsed) && string.Equals(parsed.Address, value, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object?> CompanyValues(Company c) => new()
    {
        ["ruc"] = c.Ruc,
        ["legalName"] = c.LegalName,
        ["tradeName"] = c.TradeName,
        ["fiscalAddress"] = c.FiscalAddress,
        ["ubigeo"] = c.Ubigeo,
        ["taxRegime"] = c.TaxRegime,
        ["contactEmail"] = c.ContactEmail,
        ["timeZone"] = c.TimeZone,
        ["defaultCurrency"] = c.DefaultCurrency,
        ["detractionAccount"] = c.DetractionAccount,
    };

    private static Dictionary<string, object?> EstablishmentValues(Establishment e) => new()
    {
        ["companyId"] = e.CompanyId,
        ["code"] = e.Code,
        ["name"] = e.Name,
        ["address"] = e.Address,
        ["ubigeo"] = e.Ubigeo,
        ["isActive"] = e.IsActive,
    };

    private static CompanyDto ToDto(Company c) => new(
        c.Id, c.TenantId, c.Ruc, c.LegalName, c.TradeName, c.FiscalAddress, c.Ubigeo, c.TaxRegime, c.ContactEmail,
        c.TimeZone, c.DefaultCurrency, c.Status, c.CreatedAt, c.DetractionAccount);

    private static EstablishmentDto ToDto(Establishment e) => new(e.Id, e.CompanyId, e.Code, e.Name, e.Address, e.Ubigeo, e.IsActive, e.CreatedAt);
}
