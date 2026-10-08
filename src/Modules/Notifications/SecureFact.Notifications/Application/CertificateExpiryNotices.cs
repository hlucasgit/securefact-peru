using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SecureFact.Certificates.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Notifications.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Domain;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Notifications.Application;

/// <summary>Looks for digital certificates that are about to expire, or just did, and tells the owners of their account (ADR-055). The worker runs it once a day.</summary>
public interface ICertificateExpiryNotices
{
    /// <returns>How many certificates got a notice in this pass.</returns>
    Task<int> RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// An active certificate gets one notice per threshold it crosses: 30, 15 and 7 days before it expires, and the day it does (the dedupe key names the certificate and the threshold, so a
/// daily pass never repeats one). A certificate that expired more than a month ago is left alone: it is a forgotten one, not a surprise. If the first pass sees a certificate already
/// inside a threshold, only that threshold is told, not the ones it skipped.
/// </summary>
internal sealed partial class CertificateExpiryNotices(
    ITenantAdministration tenants, IServiceScopeFactory scopes, IAccountDirectory directory, NoticeContext context, IEmailOutbox queue, DataScope scope, TimeProvider clock,
    ILogger<CertificateExpiryNotices> logger)
    : ICertificateExpiryNotices
{
    private const int ForgottenAfterDays = 30;
    private const int PageSize = 100;

    private static readonly int[] Thresholds = [30, 15, 7, 0];

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        // The accounts are listed in an explicit platform scope, with its reason; the certificates of each one are read in the scope of that account (they are tenant data: not even the
        // platform scope reads them, Row Level Security), one account at a time.
        using var elevated = scope.Elevate("notices: certificates about to expire");
        var told = 0;
        for (var skip = 0; ; skip += PageSize)
        {
            var page = await tenants.ListAsync(null, TenantStatus.Active, skip, PageSize, cancellationToken);
            if (!page.IsSuccess || page.Value.Count == 0)
            {
                break;
            }

            foreach (var tenant in page.Value)
            {
                told += await NotifyTenantAsync(tenant.Id.Value, cancellationToken);
            }

            if (page.Value.Count < PageSize)
            {
                break;
            }
        }

        return told;
    }

    private async Task<int> NotifyTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var told = 0;
        try
        {
            IReadOnlyList<CertificateDto> expiring;
            await using (var tenantScope = scopes.CreateAsyncScope())
            {
                tenantScope.ServiceProvider.GetRequiredService<DataScope>().UseTenant(new TenantId(tenantId));
                expiring = await tenantScope.ServiceProvider.GetRequiredService<ICertificateAdministration>().ListExpiringAsync(Thresholds[0], cancellationToken);
            }

            var now = clock.GetUtcNow();
            foreach (var certificate in expiring)
            {
                var daysLeft = (certificate.NotAfter - now).TotalDays;
                if (daysLeft < -ForgottenAfterDays)
                {
                    continue;
                }

                // The tightest threshold that was crossed: 7 for 3 days left, 0 once it has expired.
                var threshold = Thresholds.Where(t => daysLeft <= t).DefaultIfEmpty(-1).Min();
                if (threshold >= 0 && await NotifyAsync(certificate, threshold, daysLeft, cancellationToken))
                {
                    told++;
                }
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogFailed(logger, failure.GetType().Name);
        }

        return told;
    }

    private async Task<bool> NotifyAsync(CertificateDto certificate, int threshold, double daysLeft, CancellationToken cancellationToken)
    {
        var owners = await directory.TenantOwnerEmailsAsync(certificate.TenantId, cancellationToken);
        if (owners.Count == 0)
        {
            return false;
        }

        var (brand, portal, _) = await context.ForAsync(certificate.TenantId, null, cancellationToken);
        var expired = threshold == 0;
        var days = Math.Max(0, (int)Math.Ceiling(daysLeft));
        var unit = days == 1 ? "día" : "días";
        var when = certificate.NotAfter.UtcDateTime.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var queued = false;
        foreach (var owner in owners)
        {
            var message = NoticeEmail.Compose(
                owner, brand,
                expired ? "Su certificado digital venció" : $"Su certificado digital vence en {days} {unit}",
                [
                    expired
                        ? $"El certificado digital «{certificate.Subject}» venció el {when} (UTC). Mientras no suba uno vigente, no se pueden firmar ni emitir comprobantes de esa empresa."
                        : $"El certificado digital «{certificate.Subject}» vence el {when} (UTC), en {days} {unit}. Después de esa fecha no se podrán firmar ni emitir comprobantes de esa empresa.",
                    "Suba el certificado nuevo antes de que venza, en la ficha de la empresa.",
                ],
                "Ir a las empresas", $"{portal}/empresas");
            queued |= await queue.EnqueueAsync(message, $"certificate-expiry:{certificate.Id:N}:{threshold}", cancellationToken);
        }

        return queued;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A certificate expiry notice could not be queued ({Failure}).")]
    private static partial void LogFailed(ILogger logger, string failure);
}
