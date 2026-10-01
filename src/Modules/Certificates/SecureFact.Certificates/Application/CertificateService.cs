using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Certificates.Contracts;
using SecureFact.Certificates.Domain;
using SecureFact.Certificates.Infrastructure;
using SecureFact.Organizations.Contracts;
using SecureFact.Platform.Security;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Tenancy;

namespace SecureFact.Certificates.Application;

internal sealed partial class CertificateService(
    CertificatesDbContext db,
    IDataScope scope,
    ICompanyAdministration companies,
    ISecretProtector secrets,
    ICurrentUser user,
    TimeProvider clock,
    IAuditTrail audit) : ICertificateAdministration, ICertificateProvider
{
    public const string PfxPurpose = "certificates.pfx";
    private const int MaxPfxBytes = 100 * 1024;
    private const int MaxPage = 200;
    private const int MaxExpiryWindowDays = 3650;

    private static readonly Error Missing = Error.NotFound(ErrorCodes.CertificateNotFound, "Certificado no encontrado", "El certificado no existe o no es visible para este contexto.");

    [GeneratedRegex(@"(?<!\d)\d{11}(?!\d)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex RucLike();

    public async Task<Result<CertificateDto>> UploadAsync(UploadCertificateRequest request, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Tenant || scope.Current is not { } tenant)
        {
            return Error.Forbidden(ErrorCodes.TenantNotResolved, "Tenant requerido", "Esta operación requiere un contexto de tenant.");
        }

        var company = await companies.GetAsync(request.CompanyId, cancellationToken);
        if (!company.IsSuccess)
        {
            return company.Error;
        }

        if (!TryDecode(request.PfxBase64, out var pfx))
        {
            return Invalid("El archivo del certificado no es base64 válido o excede el tamaño permitido.");
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12(pfx, request.Password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            // Same message for a wrong password and a corrupt file: never help guess the password.
            return Invalid("No se pudo abrir el certificado. Verifique el archivo y la contraseña.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }

        using (certificate)
        {
            if (Validate(certificate, company.Value.Ruc) is { } problem)
            {
                return problem;
            }

            var thumbprint = certificate.Thumbprint;
            if (await db.Certificates.AnyAsync(c => c.CompanyId == request.CompanyId && c.Thumbprint == thumbprint, cancellationToken))
            {
                return Error.Conflict(ErrorCodes.CertificateAlreadyExists, "Certificado existente", "Este certificado ya fue cargado para la empresa.");
            }

            var plain = certificate.Export(X509ContentType.Pkcs12);
            byte[] protectedPfx;
            try
            {
                protectedPfx = secrets.Protect(plain, PfxPurpose);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }

            var now = clock.GetUtcNow();
            var entity = CompanyCertificate.Create(
                Guid.CreateVersion7(), tenant.Value, request.CompanyId, certificate.Subject, thumbprint, certificate.SerialNumber,
                certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime(), protectedPfx,
                RucMatches(certificate.Subject, company.Value.Ruc) == true, user.UserId, now);

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var previous = await db.Certificates.Where(c => c.CompanyId == request.CompanyId && c.IsActive).ToListAsync(cancellationToken);
            foreach (var old in previous)
            {
                old.Deactivate(now);
            }

            // Saved apart so the old one is inactive before the new one is inserted (partial unique index on active rows).
            await db.SaveChangesAsync(cancellationToken);
            db.Certificates.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await audit.RecordAsync(new AuditEvent(
                AuditActions.CertificateUploaded, "certificate", entity.Id.ToString("D"), tenant.Value,
                NewValues: Values(entity, previous.Select(p => p.Thumbprint))), cancellationToken);
            return ToDto(entity);
        }
    }

    public async Task<IReadOnlyList<CertificateDto>> ListAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var rows = await db.Certificates.AsNoTracking().Where(c => c.CompanyId == companyId)
            .OrderByDescending(c => c.CreatedAt).Take(MaxPage).ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<CertificateDto>> ListExpiringAsync(int days, CancellationToken cancellationToken)
    {
        var limit = clock.GetUtcNow().AddDays(Math.Clamp(days, 0, MaxExpiryWindowDays));
        var rows = await db.Certificates.AsNoTracking().Where(c => c.IsActive && c.NotAfter <= limit)
            .OrderBy(c => c.NotAfter).Take(MaxPage).ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<Unit>> DeactivateAsync(Guid certificateId, CancellationToken cancellationToken)
    {
        var entity = await db.Certificates.SingleOrDefaultAsync(c => c.Id == certificateId, cancellationToken);
        if (entity is null)
        {
            return Missing;
        }

        if (entity.IsActive)
        {
            entity.Deactivate(clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(new AuditEvent(
                AuditActions.CertificateDeactivated, "certificate", entity.Id.ToString("D"), entity.TenantId,
                OldValues: new Dictionary<string, object?> { ["thumbprint"] = entity.Thumbprint, ["isActive"] = true },
                NewValues: new Dictionary<string, object?> { ["thumbprint"] = entity.Thumbprint, ["isActive"] = false }), cancellationToken);
        }

        return Unit.Value;
    }

    public async Task<Result<X509Certificate2>> GetActiveSigningCertificateAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var entity = await db.Certificates.AsNoTracking().SingleOrDefaultAsync(c => c.CompanyId == companyId && c.IsActive, cancellationToken);
        var now = clock.GetUtcNow();
        if (entity is null || now < entity.NotBefore || now > entity.NotAfter)
        {
            return Error.Conflict(ErrorCodes.CertificateUnavailable, "Certificado no disponible", "La empresa no tiene un certificado digital activo y vigente.");
        }

        byte[] plain;
        try
        {
            plain = secrets.Unprotect(entity.ProtectedPfx, PfxPurpose);
        }
        catch (CryptographicException)
        {
            return Error.Conflict(ErrorCodes.CertificateUnavailable, "Certificado no disponible", "No se pudo recuperar el certificado almacenado.");
        }

        try
        {
            return X509CertificateLoader.LoadPkcs12(plain, null, X509KeyStorageFlags.EphemeralKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private Error? Validate(X509Certificate2 certificate, string companyRuc)
    {
        if (!certificate.HasPrivateKey)
        {
            return Invalid("El archivo no contiene la clave privada.");
        }

        using var rsa = certificate.GetRSAPrivateKey();
        if (rsa is null || rsa.KeySize < 2048)
        {
            return Invalid("Se requiere un certificado RSA de al menos 2048 bits.");
        }

        var now = clock.GetUtcNow();
        if (now < certificate.NotBefore || now > certificate.NotAfter)
        {
            return Invalid("El certificado no está vigente.");
        }

        return RucMatches(certificate.Subject, companyRuc) == false
            ? Invalid("El certificado identifica a otro contribuyente (RUC distinto al de la empresa).")
            : null;
    }

    /// <summary>True: the subject carries the company's RUC. False: it carries only other RUC-like numbers. Null: no RUC-like number at all.</summary>
    internal static bool? RucMatches(string subject, string companyRuc)
    {
        var found = RucLike().Matches(subject).Select(m => m.Value).ToList();
        return found.Count == 0 ? null : found.Contains(companyRuc, StringComparer.Ordinal);
    }

    private static bool TryDecode(string base64, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(base64) || base64.Length > (MaxPfxBytes * 4 / 3) + 8)
        {
            return false;
        }

        var buffer = new byte[MaxPfxBytes + 3];
        if (!Convert.TryFromBase64String(base64.Trim(), buffer, out var written) || written is 0 or > MaxPfxBytes)
        {
            return false;
        }

        bytes = buffer.AsSpan(0, written).ToArray();
        CryptographicOperations.ZeroMemory(buffer);
        return true;
    }

    private static Error Invalid(string detail) => Error.Validation(ErrorCodes.InvalidCertificate, "Certificado no válido", detail);

    private static CertificateDto ToDto(CompanyCertificate c) => new(
        c.Id, c.TenantId, c.CompanyId, c.Subject, c.Thumbprint, c.SerialNumber, c.NotBefore, c.NotAfter, c.IsActive, c.RucInSubject, c.CreatedAt, c.DeactivatedAt);

    // The audit trail records identification facts only: never key material, the password or the encrypted blob.
    private static Dictionary<string, object?> Values(CompanyCertificate c, IEnumerable<string> replaced) => new()
    {
        ["companyId"] = c.CompanyId,
        ["subject"] = c.Subject,
        ["thumbprint"] = c.Thumbprint,
        ["notAfter"] = c.NotAfter,
        ["replacedThumbprints"] = replaced.ToArray(),
    };
}
