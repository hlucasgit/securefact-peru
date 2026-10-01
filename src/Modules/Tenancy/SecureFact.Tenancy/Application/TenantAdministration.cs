using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;
using SecureFact.Tenancy.Contracts;
using SecureFact.Tenancy.Domain;
using SecureFact.Tenancy.Infrastructure;

namespace SecureFact.Tenancy.Application;

internal sealed class TenantAdministration(TenancyDbContext db, IDataScope scope, TimeProvider clock, IAuditTrail audit) : ITenantAdministration
{
    private const int MinNameLength = 3;
    private const int MaxNameLength = 120;

    public async Task<Result<TenantDto>> CreateAsync(CreateTenantRequest request, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Platform)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Solo la plataforma puede crear tenants.");
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < MinNameLength or > MaxNameLength)
        {
            return Error.Validation(
                ErrorCodes.InvalidTenantName,
                "Nombre de tenant inválido",
                $"El nombre debe tener entre {MinNameLength} y {MaxNameLength} caracteres.");
        }

        var tenant = Tenant.Create(TenantId.New().Value, name, request.Environment, request.ResellerId, clock.GetUtcNow());
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.TenantCreated, "tenant", tenant.Id.ToString("D"), tenant.Id,
                NewValues: new Dictionary<string, object?> { ["name"] = tenant.Name, ["environment"] = tenant.Environment, ["resellerId"] = tenant.ResellerId }),
            cancellationToken);
        return ToDto(tenant);
    }

    public async Task<Result<TenantDto>> GetAsync(TenantId id, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id.Value, cancellationToken);
        return tenant is null
            ? Error.NotFound(ErrorCodes.TenantNotFound, "Tenant no encontrado", "El tenant no existe o no es visible para este contexto.")
            : ToDto(tenant);
    }

    private static TenantDto ToDto(Tenant t) => new(new TenantId(t.Id), t.Name, t.Status, t.Environment, t.ResellerId, t.CreatedAt);
}
