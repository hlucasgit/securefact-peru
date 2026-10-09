using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Subscriptions.Contracts;
using SecureFact.Subscriptions.Domain;
using SecureFact.Subscriptions.Infrastructure;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Subscriptions.Application;

internal sealed class BillingProfiles(SubscriptionsDbContext db, IDataScope scope, ICurrentUser actor, TimeProvider clock, IAuditTrail audit, ITenantAdministration tenants) : IBillingProfiles
{
    private const int MaxNameLength = 200;
    private const int MinNameLength = 3;
    private const int MaxEmailLength = 254;

    // The same answer for a tenant that does not exist and for one that is not the caller's.
    private static readonly Error Missing = Error.NotFound(ErrorCodes.TenantNotFound, "Tenant no encontrado", "El tenant no existe o no es visible para este contexto.");

    public async Task<Result<BillingProfileDto>> GetAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        if (!CanAccess(tenantId))
        {
            return Missing;
        }

        var profile = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.TenantId == tenantId.Value, cancellationToken);
        return profile is null
            ? Error.NotFound(ErrorCodes.InvalidBillingProfile, "Sin datos de facturación", "La cuenta aún no tiene datos de facturación: sin ellos no se le emite factura ni boleta.")
            : ToDto(profile);
    }

    public async Task<Result<BillingProfileDto>> SetAsync(TenantId tenantId, BillingProfileInput input, CancellationToken cancellationToken)
    {
        if (!CanAccess(tenantId))
        {
            return Missing;
        }

        if (Validate(input) is { } invalid)
        {
            return invalid;
        }

        var tenant = await tenants.GetAsync(tenantId, cancellationToken);
        if (!tenant.IsSuccess)
        {
            return tenant.Error;
        }

        var now = clock.GetUtcNow();
        var profile = await db.Profiles.SingleOrDefaultAsync(p => p.TenantId == tenantId.Value, cancellationToken);
        if (profile is null)
        {
            profile = BillingProfile.Create(tenantId.Value, input, now);
            db.Profiles.Add(profile);
        }
        else
        {
            profile.Apply(input, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.BillingProfileSet, "billing_profile", tenantId.Value.ToString("D"), tenantId.Value,
                NewValues: new Dictionary<string, object?> { ["documentType"] = profile.DocumentTypeCode, ["documentNumber"] = profile.DocumentNumber, ["legalName"] = profile.LegalName }),
            cancellationToken);
        return ToDto(profile);
    }

    internal static BillingProfileDto ToDto(BillingProfile p) => new(p.TenantId, p.DocumentTypeCode, p.DocumentNumber, p.LegalName, p.Address, p.Email, p.UpdatedAt);

    private static Error? Validate(BillingProfileInput input)
    {
        var type = input.DocumentTypeCode?.Trim();
        var number = input.DocumentNumber?.Trim() ?? string.Empty;
        if (type == "6")
        {
            if (!Ruc.Create(number).IsSuccess)
            {
                return Invalid("El RUC debe tener 11 dígitos y un dígito verificador correcto.");
            }
        }
        else if (type == "1")
        {
            if (number.Length != 8 || !number.All(char.IsAsciiDigit))
            {
                return Invalid("El DNI debe tener 8 dígitos.");
            }
        }
        else
        {
            return Invalid("Se factura a un RUC (tipo 6, se emite factura) o a un DNI (tipo 1, se emite boleta de venta).");
        }

        var name = input.LegalName?.Trim() ?? string.Empty;
        if (name.Length is < MinNameLength or > MaxNameLength)
        {
            return Invalid($"El nombre o razón social tiene de {MinNameLength} a {MaxNameLength} caracteres.");
        }

        if (input.Address?.Trim().Length > MaxNameLength)
        {
            return Invalid($"La dirección tiene hasta {MaxNameLength} caracteres.");
        }

        if (!string.IsNullOrWhiteSpace(input.Email) && (input.Email.Trim().Length > MaxEmailLength || !input.Email.Contains('@', StringComparison.Ordinal) || input.Email.Contains(' ', StringComparison.Ordinal)))
        {
            return Invalid("El correo no tiene una forma válida.");
        }

        return null;
    }

    private static Error Invalid(string detail) => Error.Validation(ErrorCodes.InvalidBillingProfile, "Datos de facturación inválidos", detail);

    private bool CanAccess(TenantId tenantId) =>
        (scope.Kind == DataScopeKind.Platform && actor.IsPlatform) || (scope.Kind == DataScopeKind.Tenant && scope.Current == tenantId);
}
