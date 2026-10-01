using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Certificates.Contracts;
using SecureFact.Certificates.Domain;
using SecureFact.Certificates.Infrastructure;
using SecureFact.Organizations.Contracts;
using SecureFact.Platform.Security;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Tenancy;

namespace SecureFact.Certificates.Application;

internal sealed class SolCredentialService(
    CertificatesDbContext db,
    IDataScope scope,
    ICompanyAdministration companies,
    ISecretProtector secrets,
    ICurrentUser user,
    TimeProvider clock,
    IAuditTrail audit) : ISolCredentialAdministration, ISolCredentialProvider
{
    public const string PasswordPurpose = "certificates.sol-password";
    private const int MaxUserLength = 30;
    private const int MaxPasswordLength = 100;

    private static readonly Error Missing = Error.NotFound(ErrorCodes.CertificateNotFound, "Credenciales no encontradas", "La empresa no tiene credenciales SOL registradas.");

    public async Task<Result<SolCredentialDto>> SetAsync(SetSolCredentialsRequest request, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Tenant || scope.Current is not { } tenant)
        {
            return Error.Forbidden(ErrorCodes.TenantNotResolved, "Tenant requerido", "Esta operación requiere un contexto de tenant.");
        }

        var solUser = request.SolUser?.Trim() ?? string.Empty;
        if (solUser.Length is 0 or > MaxUserLength || solUser.Any(char.IsWhiteSpace) || string.IsNullOrEmpty(request.SolPassword) || request.SolPassword.Length > MaxPasswordLength)
        {
            return Error.Validation(ErrorCodes.InvalidCertificate, "Credenciales SOL no válidas", "Indique el usuario SOL (sin espacios) y la clave.");
        }

        var company = await companies.GetAsync(request.CompanyId, cancellationToken);
        if (!company.IsSuccess)
        {
            return company.Error;
        }

        var plain = Encoding.UTF8.GetBytes(request.SolPassword);
        byte[] protectedPassword;
        try
        {
            protectedPassword = secrets.Protect(plain, PasswordPurpose);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        var now = clock.GetUtcNow();
        var entity = await db.SolCredentials.SingleOrDefaultAsync(c => c.CompanyId == request.CompanyId, cancellationToken);
        var created = entity is null;
        if (entity is null)
        {
            entity = SolCredential.Create(Guid.CreateVersion7(), tenant.Value, request.CompanyId, solUser, protectedPassword, user.UserId, now);
            db.SolCredentials.Add(entity);
        }
        else
        {
            entity.Replace(solUser, protectedPassword, user.UserId, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(
            AuditActions.SolCredentialsSet, "sol_credential", entity.Id.ToString("D"), tenant.Value,
            NewValues: new Dictionary<string, object?> { ["companyId"] = request.CompanyId, ["solUser"] = solUser, ["created"] = created }), cancellationToken);
        return ToDto(entity);
    }

    public async Task<Result<SolCredentialDto>> GetAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var entity = await db.SolCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.CompanyId == companyId, cancellationToken);
        return entity is null ? Missing : ToDto(entity);
    }

    public async Task<Result<Unit>> ClearAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var entity = await db.SolCredentials.SingleOrDefaultAsync(c => c.CompanyId == companyId, cancellationToken);
        if (entity is null)
        {
            return Missing;
        }

        if (entity.ProtectedPassword is not null)
        {
            entity.Clear(user.UserId, clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(new AuditEvent(
                AuditActions.SolCredentialsCleared, "sol_credential", entity.Id.ToString("D"), entity.TenantId,
                OldValues: new Dictionary<string, object?> { ["companyId"] = companyId, ["solUser"] = entity.SolUser }), cancellationToken);
        }

        return Unit.Value;
    }

    async Task<Result<SolSecret>> ISolCredentialProvider.GetAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var entity = await db.SolCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.CompanyId == companyId, cancellationToken);
        if (entity?.ProtectedPassword is not { } blob)
        {
            return Error.Conflict(ErrorCodes.CertificateUnavailable, "Credenciales SOL no disponibles", "La empresa no tiene credenciales SOL registradas.");
        }

        byte[] plain;
        try
        {
            plain = secrets.Unprotect(blob, PasswordPurpose);
        }
        catch (CryptographicException)
        {
            return Error.Conflict(ErrorCodes.CertificateUnavailable, "Credenciales SOL no disponibles", "No se pudieron recuperar las credenciales almacenadas.");
        }

        try
        {
            return new SolSecret(entity.SolUser, Encoding.UTF8.GetString(plain));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static SolCredentialDto ToDto(SolCredential c) => new(c.CompanyId, c.SolUser, c.ProtectedPassword is not null, c.UpdatedAt);
}
