using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Identity.Domain;
using SecureFact.Identity.Infrastructure;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Tenancy;

namespace SecureFact.Identity.Application;

internal sealed class ApiKeyService(IdentityDbContext db, DataScope scope, ICurrentUser actor, TimeProvider clock, IAuditTrail audit) : IApiKeys
{
    private const int MaxActiveKeys = 20;
    private const int MinNameLength = 3;
    private const int MaxNameLength = 60;
    private const int SecretBytes = 32;
    private const int PrefixLength = 6;
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(366 * 2);
    private static readonly TimeSpan UsedMarkInterval = TimeSpan.FromMinutes(5);

    private static readonly Error Missing = Error.NotFound(ErrorCodes.ApiKeyNotFound, "Llave no encontrada", "La llave no existe o no es visible para este contexto.");

    public async Task<Result<CreatedApiKey>> CreateAsync(string name, string role, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        if (actor.TenantId is not { } tenantId || actor.IsPlatform)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Las llaves de API son de una cuenta: las crea una persona de esa cuenta.");
        }

        var label = name?.Trim() ?? string.Empty;
        if (label.Length is < MinNameLength or > MaxNameLength)
        {
            return Error.Validation(ErrorCodes.InvalidApiKey, "Nombre inválido", $"El nombre de la llave tiene de {MinNameLength} a {MaxNameLength} caracteres.");
        }

        if (!IApiKeys.AssignableRoles.Contains(role, StringComparer.Ordinal))
        {
            return Error.Validation(ErrorCodes.InvalidApiKey, "Rol inválido", $"Una llave puede tener uno de estos roles: {string.Join(", ", IApiKeys.AssignableRoles)}.");
        }

        // A key never has more than the person who creates it.
        if (!RoleCatalog.CanAssign(actor.Roles, actor.IsPlatform, role))
        {
            return Error.Forbidden(ErrorCodes.RoleNotAssignable, "Rol no asignable", $"No tiene permisos suficientes para dar el rol '{role}' a una llave.");
        }

        var now = clock.GetUtcNow();
        if (expiresAt is { } limit && (limit <= now || limit > now + MaxLifetime))
        {
            return Error.Validation(ErrorCodes.InvalidApiKey, "Vencimiento inválido", "La llave vence en el futuro y dentro de dos años como máximo, o no vence.");
        }

        if (await db.ApiKeys.CountAsync(k => k.RevokedAt == null && (k.ExpiresAt == null || k.ExpiresAt > now), cancellationToken) >= MaxActiveKeys)
        {
            return Error.Conflict(ErrorCodes.ApiKeyLimit, "Demasiadas llaves", $"Una cuenta tiene hasta {MaxActiveKeys} llaves activas: revoque alguna para crear otra.");
        }

        var id = Guid.CreateVersion7();
        var secret = Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));
        var key = ApiKey.Create(id, tenantId.Value, label, role, Hash(secret), secret[..PrefixLength], actor.UserId, now, expiresAt);
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.ApiKeyCreated, "api_key", id.ToString("D"), tenantId.Value,
                NewValues: new Dictionary<string, object?> { ["name"] = label, ["role"] = role, ["expiresAt"] = expiresAt }),
            cancellationToken);
        return new CreatedApiKey(ToDto(key), $"{IApiKeys.Prefix}{id:N}_{secret}");
    }

    public async Task<Result<IReadOnlyList<ApiKeyDto>>> ListAsync(CancellationToken cancellationToken)
    {
        if (actor.TenantId is null || actor.IsPlatform)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Las llaves de API son de una cuenta.");
        }

        var keys = await db.ApiKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).ThenBy(k => k.Id).ToListAsync(cancellationToken);
        return keys.Select(ToDto).ToList();
    }

    public async Task<Result<ApiKeyDto>> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        if (actor.TenantId is null || actor.IsPlatform)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Las llaves de API son de una cuenta.");
        }

        var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (key is null)
        {
            return Missing;
        }

        if (key.RevokedAt is null)
        {
            key.Revoke(clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(new AuditEvent(AuditActions.ApiKeyRevoked, "api_key", id.ToString("D"), key.TenantId, NewValues: new Dictionary<string, object?> { ["name"] = key.Name }), cancellationToken);
        }

        return ToDto(key);
    }

    public async Task<ApiKeyIdentity?> AuthenticateAsync(string presented, CancellationToken cancellationToken)
    {
        if (!TryParse(presented, out var id, out var secret))
        {
            return null;
        }

        // The caller has not been identified yet: the lookup by id is the one narrow platform-scope read that this needs.
        using var elevated = scope.Elevate("api key: authenticate");
        var key = await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id, cancellationToken);
        var now = clock.GetUtcNow();

        // The hash is compared even when the key does not exist, so the time taken does not say whether the id is real.
        var expected = key?.SecretHash ?? new byte[SHA256.HashSizeInBytes];
        var matches = CryptographicOperations.FixedTimeEquals(Hash(secret), expected);
        if (key is null || !matches || !key.IsUsable(now) || !IApiKeys.AssignableRoles.Contains(key.Role, StringComparer.Ordinal))
        {
            return null;
        }

        if (key.LastUsedAt is null || now - key.LastUsedAt > UsedMarkInterval)
        {
            await db.ApiKeys.Where(k => k.Id == id).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), cancellationToken);
        }

        return new ApiKeyIdentity(key.Id, key.TenantId, key.Role);
    }

    internal static bool TryParse(string? presented, out Guid id, out string secret)
    {
        id = Guid.Empty;
        secret = string.Empty;
        if (presented is null || !presented.StartsWith(IApiKeys.Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        // sfk_<id as 32 hex digits>_<secret>; the secret may itself contain underscores.
        var body = presented[IApiKeys.Prefix.Length..];
        const int idLength = 32;
        if (body.Length <= idLength + 1 || body[idLength] != '_' || !Guid.TryParseExact(body[..idLength], "N", out id))
        {
            return false;
        }

        secret = body[(idLength + 1)..];
        return secret.Length is >= 20 and <= 100;
    }

    private static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ApiKeyDto ToDto(ApiKey k) => new(k.Id, k.Name, k.Role, k.Prefix, k.CreatedAt, k.ExpiresAt, k.LastUsedAt, k.RevokedAt);
}
