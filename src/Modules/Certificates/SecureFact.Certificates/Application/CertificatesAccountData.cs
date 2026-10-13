using Microsoft.EntityFrameworkCore;
using SecureFact.Certificates.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.SharedKernel.Export;

namespace SecureFact.Certificates.Application;

/// <summary>
/// Which digital certificates the account loaded and when they expire (ADR-074). Never the file or its password, and not the SOL or API credentials: a private key does not leave the platform, and the
/// account that leaves still has the original certificate it bought.
/// </summary>
internal sealed class CertificatesAccountData(CertificatesDbContext db) : IAccountDataContributor
{
    public Task WriteAsync(IAccountDataWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return writer.WriteTableAsync(
            "certificados",
            ["id", "empresa_id", "titular", "huella", "serie", "valido_desde", "valido_hasta", "activo", "ruc_en_el_titular", "cargado", "desactivado"],
            db.Certificates.AsNoTracking().OrderBy(c => c.Id).InBatchesAsync(500, cancellationToken)
                .Select(c => (IReadOnlyList<string?>)[c.Id.ToString("D"), c.CompanyId.ToString("D"), c.Subject, c.Thumbprint, c.SerialNumber, AccountCsv.Stamp(c.NotBefore), AccountCsv.Stamp(c.NotAfter), AccountCsv.YesNo(c.IsActive), AccountCsv.YesNo(c.RucInSubject), AccountCsv.Stamp(c.CreatedAt), AccountCsv.Stamp(c.DeactivatedAt)]),
            cancellationToken);
    }
}
