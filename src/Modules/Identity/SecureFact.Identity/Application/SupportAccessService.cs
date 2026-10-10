using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SecureFact.Audit.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Identity.Domain;
using SecureFact.Identity.Infrastructure;
using SecureFact.Notifications.Contracts;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Identity.Application;

internal sealed class SupportAccessService(
    IdentityDbContext db,
    ICurrentUser actor,
    TimeProvider clock,
    IAuditTrail audit,
    TokenService tokens,
    ITenantAdministration tenants,
    IOptions<IdentityOptions> options,
    IBusinessNotices notices) : ISupportAccess
{
    private const int MaxNoteLength = 200;
    private const int MinReasonLength = 3;
    private const int MaxReasonLength = 300;
    private const int ListedGrants = 20;

    private static readonly Error OnlyAnAccount = Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "La autorización de soporte es de una cuenta: la da su propietario.");

    private static readonly Error NotGranted = Error.Forbidden(
        ErrorCodes.SupportAccessNotGranted, "Acceso no autorizado", "La cuenta no autorizó el acceso de soporte, o la autorización ya venció. Pídale a su propietario que la dé.");

    private static readonly Error GrantMissing = Error.NotFound(ErrorCodes.SupportGrantNotFound, "Autorización no encontrada", "La autorización no existe o no es de esta cuenta.");

    public async Task<Result<SupportGrantDto>> GrantAsync(int? hours, string? note, CancellationToken cancellationToken)
    {
        if (actor.TenantId is not { } tenantId || actor.IsPlatform || actor.IsSupportAccess)
        {
            return OnlyAnAccount;
        }

        var length = hours ?? ISupportAccess.DefaultHours;
        if (length is < ISupportAccess.MinHours or > ISupportAccess.MaxHours)
        {
            return Error.Validation(ErrorCodes.InvalidSupportAccess, "Duración inválida", $"La autorización dura de {ISupportAccess.MinHours} a {ISupportAccess.MaxHours} horas.");
        }

        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (text?.Length > MaxNoteLength)
        {
            return Error.Validation(ErrorCodes.InvalidSupportAccess, "Nota demasiado larga", $"La nota tiene hasta {MaxNoteLength} caracteres.");
        }

        var now = clock.GetUtcNow();
        if (await db.SupportGrants.AnyAsync(g => g.RevokedAt == null && g.ExpiresAt > now, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.InvalidSupportAccess, "Ya hay una autorización", "La cuenta ya tiene una autorización vigente: quítela para dar otra, o espere a que venza.");
        }

        var grant = SupportAccessGrant.Create(tenantId.Value, actor.UserId, now, now.AddHours(length), text);
        db.SupportGrants.Add(grant);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.SupportAccessGranted, "support_access", grant.Id.ToString("D"), tenantId.Value,
                NewValues: new Dictionary<string, object?> { ["hours"] = length, ["expiresAt"] = grant.ExpiresAt, ["note"] = text }),
            cancellationToken);
        return ToDto(grant, 0, null, now);
    }

    public async Task<Result<IReadOnlyList<SupportGrantDto>>> ListGrantsAsync(CancellationToken cancellationToken)
    {
        if (actor.TenantId is null || actor.IsPlatform || actor.IsSupportAccess)
        {
            return OnlyAnAccount;
        }

        var now = clock.GetUtcNow();
        var grants = await db.SupportGrants.AsNoTracking().OrderByDescending(g => g.CreatedAt).ThenBy(g => g.Id).Take(ListedGrants).ToListAsync(cancellationToken);
        var ids = grants.Select(g => g.Id).ToList();
        var entries = (await db.Sessions.AsNoTracking().Where(s => s.SupportGrantId != null && ids.Contains(s.SupportGrantId.Value))
                .GroupBy(s => s.SupportGrantId!.Value).Select(g => new { Grant = g.Key, Count = g.Count(), Last = g.Max(s => s.CreatedAt) }).ToListAsync(cancellationToken))
            .ToDictionary(e => e.Grant);
        return grants.Select(g => ToDto(g, entries.TryGetValue(g.Id, out var e) ? e.Count : 0, entries.TryGetValue(g.Id, out var last) ? last.Last : null, now)).ToList();
    }

    public async Task<Result<SupportGrantDto>> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        if (actor.TenantId is null || actor.IsPlatform || actor.IsSupportAccess)
        {
            return OnlyAnAccount;
        }

        var grant = await db.SupportGrants.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (grant is null)
        {
            return GrantMissing;
        }

        var now = clock.GetUtcNow();
        var ended = 0;
        if (grant.RevokedAt is null)
        {
            grant.Revoke(actor.UserId, now);
            foreach (var session in await db.Sessions.Where(s => s.SupportGrantId == id && s.RevokedAt == null).ToListAsync(cancellationToken))
            {
                session.Revoke("support-grant-revoked", now);
                ended++;
            }

            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(
                new AuditEvent(AuditActions.SupportAccessRevoked, "support_access", id.ToString("D"), grant.TenantId, NewValues: new Dictionary<string, object?> { ["sessionsEnded"] = ended }),
                cancellationToken);
        }

        return ToDto(grant, await db.Sessions.CountAsync(s => s.SupportGrantId == id, cancellationToken), null, now);
    }

    public async Task<Result<IReadOnlyList<AvailableSupportAccessDto>>> AvailableAsync(CancellationToken cancellationToken)
    {
        if (!actor.HasPermission(Permissions.SupportAccess) || actor.IsSupportAccess)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Solo quien da soporte entra a las cuentas.");
        }

        var now = clock.GetUtcNow();
        var grants = await db.SupportGrants.AsNoTracking().Where(g => g.RevokedAt == null && g.ExpiresAt > now).OrderBy(g => g.ExpiresAt).ToListAsync(cancellationToken);
        var available = new List<AvailableSupportAccessDto>();
        foreach (var grant in grants)
        {
            if (await VisibleTenantAsync(grant.TenantId, cancellationToken) is { } tenant)
            {
                available.Add(new AvailableSupportAccessDto(grant.TenantId, tenant.Name, grant.Id, grant.ExpiresAt));
            }
        }

        return available.OrderBy(a => a.TenantName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<Result<SupportSessionDto>> EnterAsync(Guid tenantId, string reason, ClientInfo client, CancellationToken cancellationToken)
    {
        if (!actor.HasPermission(Permissions.SupportAccess) || actor.IsSupportAccess || actor.UserId is not { } staffId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Solo quien da soporte entra a las cuentas.");
        }

        var why = reason?.Trim() ?? string.Empty;
        if (why.Length is < MinReasonLength or > MaxReasonLength)
        {
            return Error.Validation(ErrorCodes.InvalidSupportAccess, "Motivo inválido", $"Diga para qué entra, entre {MinReasonLength} y {MaxReasonLength} caracteres: el propietario lo lee.");
        }

        // A reseller reaches only its customers, and not telling a missing account from one that is not theirs keeps them from probing.
        if (await VisibleTenantAsync(tenantId, cancellationToken, requireActive: false) is not { } tenant)
        {
            return NotGranted;
        }

        if (tenant.Status != TenantStatus.Active)
        {
            return Error.Forbidden(ErrorCodes.TenantInactive, "Cuenta no disponible", "La cuenta está suspendida o cerrada: no se puede entrar.");
        }

        var now = clock.GetUtcNow();
        var grant = await db.SupportGrants.AsNoTracking().Where(g => g.TenantId == tenantId && g.RevokedAt == null && g.ExpiresAt > now).OrderByDescending(g => g.ExpiresAt).FirstOrDefaultAsync(cancellationToken);
        if (grant is null)
        {
            return NotGranted;
        }

        var ends = now.AddMinutes(options.Value.SupportSessionMinutes);
        ends = ends < grant.ExpiresAt ? ends : grant.ExpiresAt;
        var sessionId = Guid.CreateVersion7();
        var session = UserSession.StartSupport(sessionId, staffId, tenantId, grant.Id, SHA256.HashData(RandomNumberGenerator.GetBytes(32)), now, ends, client.IpAddress, client.UserAgent);
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        var viaReseller = actor.ResellerId is not null;
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.SupportAccessEntered, "support_session", sessionId.ToString("D"), tenantId,
                NewValues: new Dictionary<string, object?> { ["grant"] = grant.Id, ["reason"] = why, ["endsAt"] = ends, ["by"] = viaReseller ? "reseller" : "platform" }),
            cancellationToken);

        var name = (await db.Users.AsNoTracking().Where(u => u.Id == staffId).Select(u => u.DisplayName).FirstOrDefaultAsync(cancellationToken)) ?? "Una persona de soporte";
        await notices.SupportAccessAsync(new SupportAccessNotice(tenantId, sessionId, $"{name} ({(viaReseller ? "de su revendedor" : "de soporte de la plataforma")})", why, ends), cancellationToken);

        return new SupportSessionDto(tokens.IssueSupportToken(staffId, tenantId, sessionId, grant.Id, ends), (int)(ends - now).TotalSeconds, tenantId, tenant.Name, ends, ReadOnly: true);
    }

    /// <summary>The account as the person who supports may see it: any for the platform, only a customer for a reseller. Null otherwise.</summary>
    private async Task<TenantDto?> VisibleTenantAsync(Guid tenantId, CancellationToken cancellationToken, bool requireActive = true)
    {
        var tenant = await tenants.GetAsync(new TenantId(tenantId), cancellationToken);
        if (!tenant.IsSuccess || (actor.ResellerId is { } reseller && tenant.Value.ResellerId != reseller) || (!actor.IsPlatform && actor.ResellerId is null))
        {
            return null;
        }

        return requireActive && tenant.Value.Status != TenantStatus.Active ? null : tenant.Value;
    }

    private static SupportGrantDto ToDto(SupportAccessGrant g, int entries, DateTimeOffset? lastEntry, DateTimeOffset now) => new(
        g.Id, g.CreatedAt, g.ExpiresAt, g.RevokedAt, g.Note, g.RevokedAt is not null ? SupportGrantStatus.Revoked : g.ExpiresAt <= now ? SupportGrantStatus.Expired : SupportGrantStatus.Active, entries, lastEntry);
}
