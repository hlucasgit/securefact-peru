using Microsoft.EntityFrameworkCore;
using SecureFact.Identity.Infrastructure;
using SecureFact.Platform.Persistence;
using SecureFact.SharedKernel.Export;

namespace SecureFact.Identity.Application;

/// <summary>
/// The people of the account, their roles and its API keys (ADR-074). Never the hash of a password, the seed of the second factor, the sessions or the secret of a key: the account gets who has access
/// and with what role, not the means to enter.
/// </summary>
internal sealed class IdentityAccountData(IdentityDbContext db) : IAccountDataContributor
{
    private const int Batch = 500;

    public async Task WriteAsync(IAccountDataWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        await writer.WriteTableAsync(
            "usuarios",
            ["id", "correo", "nombre", "activo", "segundo_factor", "creado", "actualizado"],
            db.Users.AsNoTracking().OrderBy(u => u.Id).InBatchesAsync(Batch, cancellationToken)
                .Select(u => (IReadOnlyList<string?>)[u.Id.ToString("D"), u.Email, u.DisplayName, AccountCsv.YesNo(u.IsActive), AccountCsv.YesNo(u.MfaEnabled), AccountCsv.Stamp(u.CreatedAt), AccountCsv.Stamp(u.UpdatedAt)]),
            cancellationToken);
        await writer.WriteTableAsync(
            "roles_de_usuarios",
            ["usuario_id", "rol", "asignado"],
            db.UserRoles.AsNoTracking().OrderBy(r => r.UserId).ThenBy(r => r.RoleCode).InBatchesAsync(Batch, cancellationToken)
                .Select(r => (IReadOnlyList<string?>)[r.UserId.ToString("D"), r.RoleCode, AccountCsv.Stamp(r.AssignedAt)]),
            cancellationToken);
        await writer.WriteTableAsync(
            "claves_de_api",
            ["id", "nombre", "rol", "prefijo", "creada", "vence", "ultimo_uso", "revocada"],
            db.ApiKeys.AsNoTracking().OrderBy(k => k.Id).InBatchesAsync(Batch, cancellationToken)
                .Select(k => (IReadOnlyList<string?>)[k.Id.ToString("D"), k.Name, k.Role, k.Prefix, AccountCsv.Stamp(k.CreatedAt), AccountCsv.Stamp(k.ExpiresAt), AccountCsv.Stamp(k.LastUsedAt), AccountCsv.Stamp(k.RevokedAt)]),
            cancellationToken);
    }
}
