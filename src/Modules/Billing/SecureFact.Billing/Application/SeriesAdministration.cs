using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Billing.Contracts;
using SecureFact.Billing.Domain;
using SecureFact.Billing.Infrastructure;
using SecureFact.Organizations.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Billing.Application;

internal sealed class SeriesAdministration(
    BillingDbContext db,
    IDataScope scope,
    ICompanyAdministration companies,
    TimeProvider clock,
    IAuditTrail audit) : ISeriesAdministration
{
    private static readonly Error SeriesMissing = Error.NotFound(ErrorCodes.SeriesNotFound, "Serie no encontrada", "La serie no existe o no es visible para este contexto.");

    public async Task<Result<SeriesDto>> CreateAsync(CreateSeriesRequest request, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Tenant || scope.Current is not { } tenant)
        {
            return Error.Forbidden(ErrorCodes.TenantNotResolved, "Tenant requerido", "Esta operación requiere un contexto de tenant.");
        }

        var documentType = request.DocumentTypeCode?.Trim() ?? string.Empty;
        var code = request.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (BillingRules.ValidateSeriesCode(documentType, code) is { } invalid)
        {
            return invalid;
        }

        var company = await companies.GetAsync(request.CompanyId, cancellationToken);
        if (!company.IsSuccess)
        {
            return company.Error;
        }

        if (company.Value.Status != CompanyStatus.Active)
        {
            return Error.Validation(ErrorCodes.InvalidSeriesConfiguration, "Empresa inactiva", "No se pueden crear series para una empresa inactiva.");
        }

        if (request.EstablishmentId is { } establishmentId)
        {
            var establishments = await companies.ListEstablishmentsAsync(request.CompanyId, cancellationToken);
            if (!establishments.Any(e => e.Id == establishmentId && e.IsActive))
            {
                return Error.Validation(ErrorCodes.InvalidSeriesConfiguration, "Establecimiento inválido", "El establecimiento no existe, está inactivo o no pertenece a la empresa.");
            }
        }

        if (await db.Series.AnyAsync(s => s.CompanyId == request.CompanyId && s.DocumentTypeCode == documentType && s.Code == code, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.SeriesAlreadyExists, "Serie existente", "Ya existe esa serie para el tipo de documento en la empresa.");
        }

        var series = Series.Create(Guid.CreateVersion7(), tenant.Value, request.CompanyId, request.EstablishmentId, documentType, code, clock.GetUtcNow());
        db.Series.Add(series);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(
            AuditActions.SeriesCreated, "series", series.Id.ToString("D"), tenant.Value,
            NewValues: new Dictionary<string, object?> { ["companyId"] = series.CompanyId, ["documentType"] = documentType, ["code"] = code }), cancellationToken);
        return ToDto(series);
    }

    public async Task<IReadOnlyList<SeriesDto>> ListAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var rows = await db.Series.AsNoTracking().Where(s => s.CompanyId == companyId).OrderBy(s => s.DocumentTypeCode).ThenBy(s => s.Code).ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<Unit>> DeactivateAsync(Guid seriesId, CancellationToken cancellationToken)
    {
        var series = await db.Series.SingleOrDefaultAsync(s => s.Id == seriesId, cancellationToken);
        if (series is null)
        {
            return SeriesMissing;
        }

        series.Deactivate(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.SeriesDeactivated, "series", series.Id.ToString("D"), series.TenantId), cancellationToken);
        return Unit.Value;
    }

    internal static SeriesDto ToDto(Series s) =>
        new(s.Id, s.TenantId, s.CompanyId, s.EstablishmentId, s.DocumentTypeCode, s.Code, s.LastNumber, s.IsActive, s.CreatedAt);
}
