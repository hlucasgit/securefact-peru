using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SecureFact.Audit.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Results;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Tenancy.Contracts;
using SecureFact.Tenancy.Domain;
using SecureFact.Tenancy.Infrastructure;

namespace SecureFact.Tenancy.Application;

/// <summary>Names of hosts: how a host is read from what a person or a browser sends, and what a valid one looks like.</summary>
internal static partial class HostNames
{
    // Two or more labels of letters, digits and hyphens, the last one with a letter (so an IP address is not a host name).
    [GeneratedRegex(@"^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex Pattern();

    /// <summary>Lower case, without port and without the final dot; null when there is nothing.</summary>
    public static string? Normalize(string? host)
    {
        var value = host?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        return (colon >= 0 ? value[..colon] : value).TrimEnd('.');
    }

    public static bool IsValid(string? host) => host is not null && Pattern().IsMatch(host);
}

/// <summary>
/// The domain of a reseller's portal (ADR-051). The platform assigns the name; the reseller publishes two DNS records (a TXT with a secret that proves it controls the name, and a CNAME
/// to the platform's edge); the platform checks them, and from then on the brand is shown there and the edge may issue a certificate for it. A domain whose checks fail again after being
/// verified goes back to being unreachable by itself and recovers by itself.
/// </summary>
internal sealed class DomainService(TenancyDbContext db, DataScope scope, ICurrentUser actor, IDomainNameSystem dns, DomainsOptions options, TimeProvider clock, IAuditTrail audit) : IDomains
{
    /// <summary>The prefix of the TXT record that proves ownership: <c>_securefact-challenge.&lt;host&gt;</c> holds <c>securefact-verification=&lt;token&gt;</c>.</summary>
    public const string ChallengeLabel = "_securefact-challenge";

    public const string ValuePrefix = "securefact-verification=";

    private static readonly Error ResellerMissing = Error.NotFound(ErrorCodes.ResellerNotFound, "Revendedor no encontrado", "El revendedor no existe.");

    // ---------- what a reseller or the platform asks ----------

    public async Task<Result<DomainDto>> GetAsync(Guid resellerId, CancellationToken cancellationToken)
    {
        if (Authorize(resellerId) is { } denied)
        {
            return denied;
        }

        var reseller = await db.Resellers.AsNoTracking().SingleOrDefaultAsync(r => r.Id == resellerId, cancellationToken);
        return reseller is null ? ResellerMissing : ToDto(reseller);
    }

    public async Task<Result<DomainDto>> SetAsync(Guid resellerId, string? host, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Platform || !actor.IsPlatform)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Solo el personal de la plataforma asigna el dominio de un revendedor.");
        }

        string? clean = null;
        if (!string.IsNullOrWhiteSpace(host))
        {
            clean = HostNames.Normalize(host);
            if (!HostNames.IsValid(clean))
            {
                return Error.Validation(ErrorCodes.InvalidDomain, "Dominio inválido", "Indique un nombre de dominio, por ejemplo portal.ejemplo.pe, sin protocolo, puerto ni ruta.");
            }

            // The domains of the platform itself are not for a reseller to take over.
            if (options.IsPlatformName(clean))
            {
                return Error.Validation(ErrorCodes.InvalidDomain, "Dominio reservado", "Ese dominio es de la plataforma.");
            }
        }

        var reseller = await db.Resellers.SingleOrDefaultAsync(r => r.Id == resellerId, cancellationToken);
        if (reseller is null)
        {
            return ResellerMissing;
        }

        if (clean == reseller.Host)
        {
            return ToDto(reseller); // the same domain: its proof and its state stay
        }

        if (clean is not null && await db.Resellers.AnyAsync(r => r.Host == clean && r.Id != resellerId, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.HostInUse, "Dominio en uso", "Otro revendedor ya usa ese dominio.");
        }

        var before = Values(reseller);
        reseller.SetHost(clean, clean is null ? null : NewToken());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.ResellerDomainChanged, "reseller", reseller.Id.ToString("D"), null, OldValues: before, NewValues: Values(reseller)), cancellationToken);
        return ToDto(reseller);
    }

    public async Task<Result<DomainDto>> VerifyAsync(Guid resellerId, CancellationToken cancellationToken)
    {
        if (Authorize(resellerId) is { } denied)
        {
            return denied;
        }

        var reseller = await db.Resellers.SingleOrDefaultAsync(r => r.Id == resellerId, cancellationToken);
        if (reseller is null)
        {
            return ResellerMissing;
        }

        if (reseller.Host is null)
        {
            return Error.Validation(ErrorCodes.NoDomain, "Sin dominio", "El revendedor no tiene un dominio asignado.");
        }

        var now = clock.GetUtcNow();
        if (reseller.HostCheckedAt is { } last && now - last < options.MinimumCheckInterval)
        {
            return Error.Conflict(ErrorCodes.DomainCheckTooSoon, "Revisión reciente", $"El dominio se revisó hace menos de {options.MinimumCheckInterval.TotalSeconds:0} segundos: espere un poco para volver a revisar.");
        }

        await CheckAsync(reseller, now, cancellationToken);
        return ToDto(reseller);
    }

    // ---------- the background pass ----------

    public async Task<int> VerifyDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var pending = now - options.PendingRecheck;
        var settled = now - options.SettledRecheck;
        var due = await db.Resellers
            .Where(r => r.Host != null && r.IsActive
                && (r.HostStatus == DomainStatus.Pending ? r.HostCheckedAt == null || r.HostCheckedAt < pending : r.HostCheckedAt == null || r.HostCheckedAt < settled))
            .OrderBy(r => r.HostCheckedAt)
            .Take(options.MaxPerPass)
            .ToListAsync(cancellationToken);
        foreach (var reseller in due)
        {
            await CheckAsync(reseller, now, cancellationToken);
        }

        return due.Count;
    }

    // ---------- what the edge asks ----------

    public async Task<bool> IsAllowedForCertificateAsync(string host, CancellationToken cancellationToken)
    {
        var name = HostNames.Normalize(host);
        if (name is null)
        {
            return false;
        }

        if (options.IsPlatformName(name))
        {
            return true;
        }

        using var elevated = scope.Elevate("domains: certificate of a host");
        return await db.Resellers.AsNoTracking().AnyAsync(r => r.Host == name && r.HostStatus == DomainStatus.Verified && r.IsActive, cancellationToken);
    }

    // ---------- the check ----------

    /// <summary>Looks up the two records and records the outcome (and, on a change of state, in the audit trail). A resolver that does not answer counts as a failed check, in words.</summary>
    private async Task CheckAsync(Reseller reseller, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var before = reseller.HostStatus;
        string? problem;
        try
        {
            problem = await FindProblemAsync(reseller, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            problem = "No se pudo consultar el DNS en este momento. Se volverá a intentar.";
        }

        if (problem is null)
        {
            reseller.MarkVerified(now);
        }
        else
        {
            reseller.MarkCheckFailed(now, problem, options.UnreachableAfterFailures);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (before != reseller.HostStatus && reseller.HostStatus is DomainStatus.Verified or DomainStatus.Unreachable)
        {
            await audit.RecordAsync(
                new AuditEvent(
                    reseller.HostStatus == DomainStatus.Verified ? AuditActions.ResellerDomainVerified : AuditActions.ResellerDomainUnreachable, "reseller", reseller.Id.ToString("D"), null,
                    OldValues: new Dictionary<string, object?> { ["status"] = before },
                    NewValues: new Dictionary<string, object?> { ["host"] = reseller.Host, ["status"] = reseller.HostStatus, ["error"] = reseller.HostError }),
                cancellationToken);
        }
    }

    /// <summary>Null when both records are right; otherwise what is wrong, said so that the reseller can fix it.</summary>
    private async Task<string?> FindProblemAsync(Reseller reseller, CancellationToken cancellationToken)
    {
        if (options.Sandbox)
        {
            return null; // the simulator of the DNS (development and end-to-end tests): it is never allowed in production
        }

        var host = reseller.Host!;
        var expected = ValuePrefix + reseller.HostToken;
        var proofs = await dns.TxtAsync($"{ChallengeLabel}.{host}", cancellationToken);
        if (!proofs.Any(value => string.Equals(value.Trim(), expected, StringComparison.Ordinal)))
        {
            return $"No se encontró el registro TXT {ChallengeLabel}.{host} con el valor {expected}. Créelo y espere a que el DNS lo publique.";
        }

        if (string.IsNullOrWhiteSpace(options.EdgeHost) && options.EdgeAddresses.Length == 0)
        {
            return "El operador de la plataforma no configuró a dónde debe apuntar el dominio (Domains:EdgeHost).";
        }

        var route = await dns.RouteAsync(host, cancellationToken);
        var edge = HostNames.Normalize(options.EdgeHost);
        if (edge is not null && route.Cnames.Any(target => HostNames.Normalize(target) == edge))
        {
            return null;
        }

        if (options.EdgeAddresses.Length > 0 && route.Addresses.Count > 0 && route.Addresses.All(a => options.EdgeAddresses.Contains(a, StringComparer.OrdinalIgnoreCase)))
        {
            return null;
        }

        var target = edge is not null ? $"un CNAME hacia {edge}" : $"direcciones {string.Join(", ", options.EdgeAddresses)}";
        return $"{host} no apunta a la plataforma: debe tener {target}. Hoy lleva {(route.Cnames.Count > 0 ? "CNAME " + string.Join(", ", route.Cnames) : route.Addresses.Count > 0 ? "direcciones " + string.Join(", ", route.Addresses) : "ningún registro")}.";
    }

    // ---------- helpers ----------

    private Error? Authorize(Guid resellerId) =>
        scope.Kind == DataScopeKind.Platform && (actor.IsPlatform || actor.ResellerId == resellerId)
            ? null
            : Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Solo la plataforma o el propio revendedor ven el dominio.");

    private DomainDto ToDto(Reseller r) => new(
        r.Id,
        r.Host,
        r.HostStatus,
        r.Host is null ? null : $"{ChallengeLabel}.{r.Host}",
        r.Host is null ? null : ValuePrefix + r.HostToken,
        string.IsNullOrWhiteSpace(options.EdgeHost) ? null : HostNames.Normalize(options.EdgeHost),
        options.EdgeAddresses,
        r.HostVerifiedAt,
        r.HostCheckedAt,
        r.HostError);

    /// <summary>192 bits from the system's generator, in lower-case hexadecimal: not guessable, and safe in a DNS record.</summary>
    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    private static Dictionary<string, object?> Values(Reseller r) => new() { ["host"] = r.Host, ["status"] = r.HostStatus };
}
