using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Gre.Contracts;
using SecureFact.Gre.Domain;
using SecureFact.Gre.Infrastructure;
using SecureFact.Organizations.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Gre.Application;

internal sealed partial class GreSeriesAdministration(
    GreDbContext db,
    IDataScope scope,
    ICompanyAdministration companies,
    TimeProvider clock,
    IAuditTrail audit) : IGreSeriesAdministration
{
    private static readonly Error SeriesMissing = Error.NotFound(ErrorCodes.GreSeriesNotFound, "Serie no encontrada", "La serie no existe o no es visible para este contexto.");

    [GeneratedRegex("^T[A-Z0-9]{3}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SenderSeries();

    public async Task<Result<GreSeriesDto>> CreateAsync(Guid companyId, string code, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Tenant || scope.Current is not { } tenant)
        {
            return Error.Forbidden(ErrorCodes.TenantNotResolved, "Tenant requerido", "Esta operación requiere un contexto de tenant.");
        }

        var normalized = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!SenderSeries().IsMatch(normalized))
        {
            return Error.Validation(ErrorCodes.InvalidGreSeries, "Serie inválida", "La serie de la guía de remisión remitente empieza con «T» y tiene tres letras o dígitos más (por ejemplo T001).");
        }

        var company = await companies.GetAsync(companyId, cancellationToken);
        if (!company.IsSuccess)
        {
            return company.Error;
        }

        if (company.Value.Status != CompanyStatus.Active)
        {
            return Error.Validation(ErrorCodes.InvalidGreSeries, "Empresa inactiva", "No se pueden crear series para una empresa inactiva.");
        }

        if (await db.Series.AnyAsync(s => s.CompanyId == companyId && s.Code == normalized, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.GreSeriesAlreadyExists, "Serie existente", "Ya existe esa serie en la empresa.");
        }

        var series = GreSeries.Create(Guid.CreateVersion7(), tenant.Value, companyId, normalized, clock.GetUtcNow());
        db.Series.Add(series);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(
            AuditActions.GreSeriesCreated, "gre_series", series.Id.ToString("D"), tenant.Value,
            NewValues: new Dictionary<string, object?> { ["companyId"] = series.CompanyId, ["code"] = normalized }), cancellationToken);
        return ToDto(series);
    }

    public async Task<IReadOnlyList<GreSeriesDto>> ListAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var rows = await db.Series.AsNoTracking().Where(s => s.CompanyId == companyId).OrderBy(s => s.Code).ToListAsync(cancellationToken);
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
        await audit.RecordAsync(new AuditEvent(AuditActions.GreSeriesDeactivated, "gre_series", series.Id.ToString("D"), series.TenantId), cancellationToken);
        return Unit.Value;
    }

    private static GreSeriesDto ToDto(GreSeries s) => new(s.Id, s.CompanyId, s.Code, s.LastNumber, s.IsActive, s.CreatedAt);
}
