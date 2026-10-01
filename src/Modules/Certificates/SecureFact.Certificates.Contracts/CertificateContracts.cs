using System.Security.Cryptography.X509Certificates;
using SecureFact.SharedKernel.Results;

namespace SecureFact.Certificates.Contracts;

/// <summary>
/// Digital certificate upload. <see cref="PfxBase64"/> is a PKCS#12 file; <see cref="Password"/> opens it.
/// Neither is ever stored in clear, logged or returned: <see cref="ToString"/> prints neither.
/// </summary>
public sealed record UploadCertificateRequest(Guid CompanyId, string PfxBase64, string Password)
{
    public override string ToString() => $"UploadCertificateRequest {{ CompanyId = {CompanyId}, PfxBase64 = *****, Password = ***** }}";
}

/// <summary>Public facts about a stored certificate. The key material is never part of any DTO.</summary>
/// <param name="RucInSubject">True when the subject carries the company's RUC. A subject with no RUC at all is accepted but flagged (R-036).</param>
public sealed record CertificateDto(
    Guid Id,
    Guid TenantId,
    Guid CompanyId,
    string Subject,
    string Thumbprint,
    string SerialNumber,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    bool IsActive,
    bool RucInSubject,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeactivatedAt);

public interface ICertificateAdministration
{
    /// <summary>Validates, encrypts and stores the certificate and makes it the active one of the company (the previous one is kept, deactivated).</summary>
    Task<Result<CertificateDto>> UploadAsync(UploadCertificateRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<CertificateDto>> ListAsync(Guid companyId, CancellationToken cancellationToken);

    /// <summary>Active certificates that expire within <paramref name="days"/> days (or already expired), for renewal alerts.</summary>
    Task<IReadOnlyList<CertificateDto>> ListExpiringAsync(int days, CancellationToken cancellationToken);

    /// <summary>Certificates are never deleted: signed documents must stay verifiable. Deactivation only stops new signing.</summary>
    Task<Result<Unit>> DeactivateAsync(Guid certificateId, CancellationToken cancellationToken);
}

/// <summary>Gives the signing pipeline the active certificate of a company. The caller must dispose the certificate.</summary>
public interface ICertificateProvider
{
    /// <summary>Fails with <c>SF-CRT-004</c> when the company has no active certificate or it is not valid now.</summary>
    Task<Result<X509Certificate2>> GetActiveSigningCertificateAsync(Guid companyId, CancellationToken cancellationToken);
}

/// <summary>
/// SOL user and password of the company, used to authenticate against SUNAT's billService (the password is secondary-user "Clave SOL").
/// Never stored in clear, logged or returned: <see cref="ToString"/> prints no secret.
/// </summary>
public sealed record SetSolCredentialsRequest(Guid CompanyId, string SolUser, string SolPassword)
{
    public override string ToString() => $"SetSolCredentialsRequest {{ CompanyId = {CompanyId}, SolUser = {SolUser}, SolPassword = ***** }}";
}

/// <summary>What is known about the stored SOL credentials. The password is never part of it.</summary>
public sealed record SolCredentialDto(Guid CompanyId, string SolUser, bool HasPassword, DateTimeOffset UpdatedAt);

public interface ISolCredentialAdministration
{
    /// <summary>Stores (or replaces) the SOL credentials of a company, encrypted.</summary>
    Task<Result<SolCredentialDto>> SetAsync(SetSolCredentialsRequest request, CancellationToken cancellationToken);

    Task<Result<SolCredentialDto>> GetAsync(Guid companyId, CancellationToken cancellationToken);

    /// <summary>Wipes the stored password. The user name is kept as a record that credentials existed.</summary>
    Task<Result<Unit>> ClearAsync(Guid companyId, CancellationToken cancellationToken);
}

/// <summary>The decrypted secret, for the submission pipeline only. <see cref="ToString"/> prints no secret.</summary>
public sealed record SolSecret(string SolUser, string SolPassword)
{
    public override string ToString() => $"SolSecret {{ SolUser = {SolUser}, SolPassword = ***** }}";
}

public interface ISolCredentialProvider
{
    /// <summary>Fails with <c>SF-CRT-004</c> when the company has no stored SOL credentials.</summary>
    Task<Result<SolSecret>> GetAsync(Guid companyId, CancellationToken cancellationToken);
}
