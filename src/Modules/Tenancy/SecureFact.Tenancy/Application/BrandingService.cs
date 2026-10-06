using System.Globalization;
using System.Net.Mail;
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

internal sealed partial class BrandingService(TenancyDbContext db, DataScope scope, ICurrentUser actor, IAuditTrail audit) : IBranding
{
    public const int MaxLogoBytes = 200 * 1024;

    /// <summary>WCAG 2.x level AA for normal text: the white text on the accent colour.</summary>
    public const double MinContrast = 4.5;

    private const int MinNameLength = 2;
    private const int MaxNameLength = 60;

    private static readonly Error ResellerMissing = Error.NotFound(ErrorCodes.ResellerNotFound, "Revendedor no encontrado", "El revendedor no existe.");

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    // Two or more labels of letters, digits and hyphens, the last one with a letter (so an IP address is not a host name).
    [GeneratedRegex(@"^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex HostPattern();

    // ---------- what any visitor may read ----------

    public async Task<BrandingDto?> ForHostAsync(string host, CancellationToken cancellationToken)
    {
        var clean = NormalizeHost(host);
        if (clean is null)
        {
            return null;
        }

        using var elevated = scope.Elevate("branding: portal of a host");
        var reseller = await db.Resellers.AsNoTracking().SingleOrDefaultAsync(r => r.Host == clean, cancellationToken);
        return Effective(reseller);
    }

    public async Task<BrandingDto?> ForTenantAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        using var elevated = scope.Elevate("branding: brand of a tenant");
        var resellerId = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId.Value).Select(t => t.ResellerId).SingleOrDefaultAsync(cancellationToken);
        return resellerId is { } id ? Effective(await db.Resellers.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken)) : null;
    }

    public async Task<BrandingDto?> ForResellerAsync(Guid resellerId, CancellationToken cancellationToken)
    {
        using var elevated = scope.Elevate("branding: brand of a reseller");
        return Effective(await db.Resellers.AsNoTracking().SingleOrDefaultAsync(r => r.Id == resellerId, cancellationToken));
    }

    public async Task<BrandLogo?> LogoAsync(Guid resellerId, CancellationToken cancellationToken)
    {
        using var elevated = scope.Elevate("branding: logo of a reseller");
        var reseller = await db.Resellers.AsNoTracking().SingleOrDefaultAsync(r => r.Id == resellerId, cancellationToken);
        // The logo of a reseller that is off, or that has no brand, is not served: the visitor sees the default look.
        return Effective(reseller) is not null && reseller!.Logo is { } data && reseller.LogoContentType is { } type && reseller.LogoVersion is { } version
            ? new BrandLogo(data, type, version)
            : null;
    }

    // ---------- the platform and the reseller itself ----------

    public async Task<Result<BrandingSettings>> GetAsync(Guid resellerId, CancellationToken cancellationToken)
    {
        if (Authorize(resellerId) is { } denied)
        {
            return denied;
        }

        var reseller = await db.Resellers.AsNoTracking().SingleOrDefaultAsync(r => r.Id == resellerId, cancellationToken);
        return reseller is null ? ResellerMissing : ToSettings(reseller);
    }

    public async Task<Result<BrandingSettings>> UpdateAsync(Guid resellerId, BrandingInput input, CancellationToken cancellationToken)
    {
        if (Authorize(resellerId) is { } denied)
        {
            return denied;
        }

        var name = input.BrandName?.Trim();
        var color = input.PrimaryColor?.Trim().ToLowerInvariant();
        var email = input.SupportEmail?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            // Removing the brand removes its colour and contact too: a colour without a name would never be shown.
            name = null;
            color = null;
            email = null;
        }
        else if (Validate(name, color, email) is { } invalid)
        {
            return invalid;
        }

        var reseller = await db.Resellers.SingleOrDefaultAsync(r => r.Id == resellerId, cancellationToken);
        if (reseller is null)
        {
            return ResellerMissing;
        }

        var before = Values(reseller);
        reseller.SetBrand(name, color, string.IsNullOrEmpty(email) ? null : email);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.ResellerBrandingUpdated, "reseller", reseller.Id.ToString("D"), null, OldValues: before, NewValues: Values(reseller)), cancellationToken);
        return ToSettings(reseller);
    }

    public async Task<Result<BrandingSettings>> SetHostAsync(Guid resellerId, string? host, CancellationToken cancellationToken)
    {
        if (scope.Kind != DataScopeKind.Platform || !actor.IsPlatform)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Solo el personal de la plataforma asigna el dominio de un revendedor.");
        }

        string? clean = null;
        if (!string.IsNullOrWhiteSpace(host))
        {
            clean = NormalizeHost(host);
            if (clean is null || !HostPattern().IsMatch(clean))
            {
                return Error.Validation(ErrorCodes.InvalidBranding, "Dominio inválido", "Indique un nombre de dominio, por ejemplo portal.ejemplo.pe, sin protocolo ni puerto.");
            }
        }

        var reseller = await db.Resellers.SingleOrDefaultAsync(r => r.Id == resellerId, cancellationToken);
        if (reseller is null)
        {
            return ResellerMissing;
        }

        if (clean is not null && await db.Resellers.AnyAsync(r => r.Host == clean && r.Id != resellerId, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.HostInUse, "Dominio en uso", "Otro revendedor ya usa ese dominio.");
        }

        var before = Values(reseller);
        reseller.SetHost(clean);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.ResellerBrandingUpdated, "reseller", reseller.Id.ToString("D"), null, OldValues: before, NewValues: Values(reseller)), cancellationToken);
        return ToSettings(reseller);
    }

    public async Task<Result<BrandingSettings>> SetLogoAsync(Guid resellerId, byte[] data, CancellationToken cancellationToken)
    {
        if (Authorize(resellerId) is { } denied)
        {
            return denied;
        }

        if (data is null || data.Length == 0 || data.Length > MaxLogoBytes)
        {
            return Error.Validation(ErrorCodes.InvalidLogo, "Logotipo inválido", $"El logotipo debe pesar entre 1 byte y {MaxLogoBytes / 1024} KB.");
        }

        if (DetectImage(data) is not { } contentType)
        {
            return Error.Validation(ErrorCodes.InvalidLogo, "Logotipo inválido", "El logotipo debe ser una imagen PNG, JPEG o WebP.");
        }

        var reseller = await db.Resellers.SingleOrDefaultAsync(r => r.Id == resellerId, cancellationToken);
        if (reseller is null)
        {
            return ResellerMissing;
        }

        var version = Convert.ToHexStringLower(SHA256.HashData(data))[..12];
        reseller.SetLogo(data, contentType, version);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEvent(
                AuditActions.ResellerLogoChanged, "reseller", reseller.Id.ToString("D"), null,
                NewValues: new Dictionary<string, object?> { ["logoVersion"] = version, ["contentType"] = contentType, ["bytes"] = data.Length }),
            cancellationToken);
        return ToSettings(reseller);
    }

    public async Task<Result<BrandingSettings>> RemoveLogoAsync(Guid resellerId, CancellationToken cancellationToken)
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

        reseller.SetLogo(null, null, null);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEvent(AuditActions.ResellerLogoChanged, "reseller", reseller.Id.ToString("D"), null, NewValues: new Dictionary<string, object?> { ["logoVersion"] = null }), cancellationToken);
        return ToSettings(reseller);
    }

    // ---------- rules ----------

    /// <summary>Platform staff for any reseller; a reseller user only for its own.</summary>
    private Error? Authorize(Guid resellerId) =>
        scope.Kind == DataScopeKind.Platform && (actor.IsPlatform || actor.ResellerId == resellerId)
            ? null
            : Error.Forbidden(ErrorCodes.Forbidden, "Operación no permitida", "Solo la plataforma o el propio revendedor administran su marca.");

    private static Error? Validate(string name, string? color, string? email)
    {
        if (name.Length is < MinNameLength or > MaxNameLength || name.Any(char.IsControl))
        {
            return Error.Validation(ErrorCodes.InvalidBranding, "Nombre de marca inválido", $"El nombre debe tener entre {MinNameLength} y {MaxNameLength} caracteres, sin caracteres de control.");
        }

        if (color is null || !ColorPattern().IsMatch(color))
        {
            return Error.Validation(ErrorCodes.InvalidBranding, "Color inválido", "El color se indica como #rrggbb.");
        }

        var ratio = ContrastAgainstWhite(color);
        if (ratio < MinContrast)
        {
            return Error.Validation(
                ErrorCodes.InvalidBranding, "Color con poco contraste",
                $"Con el texto blanco el color {color} tiene un contraste de {ratio.ToString("0.0", CultureInfo.InvariantCulture)}:1 y se pide al menos {MinContrast.ToString("0.0", CultureInfo.InvariantCulture)}:1. Elija un color más oscuro.");
        }

        if (!string.IsNullOrEmpty(email) && (email.Length > 254 || !MailAddress.TryCreate(email, out _)))
        {
            return Error.Validation(ErrorCodes.InvalidBranding, "Correo de soporte inválido", "Ingrese un correo electrónico válido.");
        }

        return null;
    }

    /// <summary>The contrast ratio of the colour against white, as WCAG 2.x defines it: (1.05) / (L + 0.05), with L the relative luminance.</summary>
    public static double ContrastAgainstWhite(string hex)
    {
        static double Channel(string pair)
        {
            var value = int.Parse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        var luminance = (0.2126 * Channel(hex[1..3])) + (0.7152 * Channel(hex[3..5])) + (0.0722 * Channel(hex[5..7]));
        return 1.05 / (luminance + 0.05);
    }

    /// <summary>The image type by its first bytes; what the caller says the file is does not count.</summary>
    public static string? DetectImage(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (data.Length >= 12 && data[..4].SequenceEqual<byte>("RIFF"u8) && data[8..12].SequenceEqual<byte>("WEBP"u8))
        {
            return "image/webp";
        }

        return null;
    }

    private static string? NormalizeHost(string? host)
    {
        var value = host?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        // A browser reports host:port; the portal is identified by the host alone.
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        return (colon >= 0 ? value[..colon] : value).TrimEnd('.');
    }

    /// <summary>Only an active reseller that has set a brand is shown; otherwise the default look of the platform.</summary>
    private static BrandingDto? Effective(Reseller? r) =>
        r is { IsActive: true, BrandName: { } name, PrimaryColor: { } color }
            ? new BrandingDto(r.Id, name, color, r.SupportEmail, r.LogoVersion)
            : null;

    private static BrandingSettings ToSettings(Reseller r) => new(r.Id, r.Name, r.BrandName, r.PrimaryColor, r.SupportEmail, r.Host, r.LogoVersion);

    private static Dictionary<string, object?> Values(Reseller r) => new()
    {
        ["brandName"] = r.BrandName,
        ["primaryColor"] = r.PrimaryColor,
        ["supportEmail"] = r.SupportEmail,
        ["host"] = r.Host,
    };
}
