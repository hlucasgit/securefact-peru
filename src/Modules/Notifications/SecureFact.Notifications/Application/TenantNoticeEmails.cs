using Microsoft.Extensions.Logging;
using SecureFact.Identity.Contracts;
using SecureFact.Notifications.Contracts;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Notifications.Application;

/// <summary>
/// The notices about an account and about the domain of a reseller (ADR-054), as queued e-mails. A notice is best-effort: if composing or queueing it fails, the change that caused it
/// stands and the failure is logged (the type only). The reason of a suspension is not in the e-mail: it stays in the audit trail, as the reseller does not see the platform's either (ADR-045).
/// </summary>
internal sealed partial class TenantNoticeEmails(IAccountDirectory directory, NoticeContext context, IEmailOutbox queue, ILogger<TenantNoticeEmails> logger) : ITenantNotices
{
    public async Task StatusChangedAsync(TenantStatusNotice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        try
        {
            var owners = await directory.TenantOwnerEmailsAsync(notice.TenantId, cancellationToken);
            if (owners.Count == 0)
            {
                return;
            }

            var (brand, portal, _) = await context.ForAsync(notice.TenantId, notice.ResellerId, cancellationToken);
            foreach (var owner in owners)
            {
                await queue.EnqueueAsync(Status(owner, brand, portal, notice), cancellationToken: cancellationToken);
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogNotQueued(logger, "tenant status", failure.GetType().Name);
        }
    }

    public async Task DomainChangedAsync(DomainNotice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (notice.Host is null || notice.Status == DomainStatus.None)
        {
            return;
        }

        try
        {
            var admins = await directory.ResellerAdminEmailsAsync(notice.ResellerId, cancellationToken);
            if (admins.Count == 0)
            {
                return;
            }

            var (brand, portal, _) = await context.ForAsync(null, notice.ResellerId, cancellationToken);
            foreach (var admin in admins)
            {
                await queue.EnqueueAsync(Domain(admin, brand, portal, notice), cancellationToken: cancellationToken);
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogNotQueued(logger, "domain", failure.GetType().Name);
        }
    }

    private static EmailMessage Status(string to, EmailBrand brand, string portal, TenantStatusNotice notice) => notice.Status switch
    {
        TenantStatus.Suspended => NoticeEmail.Compose(
            to, brand, $"La cuenta «{notice.TenantName}» fue suspendida",
            [
                $"La cuenta «{notice.TenantName}» en {brand.Name} fue suspendida. Mientras siga suspendida nadie de la cuenta puede ingresar ni emitir comprobantes.",
                "Sus datos y los comprobantes ya emitidos se conservan. Para conocer el motivo o pedir que se reactive, comuníquese con quien le brinda el servicio.",
            ]),
        TenantStatus.Closed => NoticeEmail.Compose(
            to, brand, $"La cuenta «{notice.TenantName}» fue cerrada",
            [
                $"La cuenta «{notice.TenantName}» en {brand.Name} fue cerrada de forma definitiva. Ya nadie puede ingresar a ella.",
                "Los comprobantes emitidos y el registro de auditoría se conservan.",
            ]),
        _ => NoticeEmail.Compose(
            to, brand, $"La cuenta «{notice.TenantName}» fue reactivada",
            [$"La cuenta «{notice.TenantName}» en {brand.Name} fue reactivada. Ya puede ingresar y emitir comprobantes."],
            "Ingresar", $"{portal}/ingresar"),
    };

    private static EmailMessage Domain(string to, EmailBrand brand, string portal, DomainNotice notice) => notice.Status switch
    {
        DomainStatus.Verified => NoticeEmail.Compose(
            to, brand, $"Su dominio {notice.Host} quedó verificado",
            [$"El dominio «{notice.Host}» quedó verificado: su portal ya muestra su marca en ese dominio y puede recibir su certificado de seguridad."],
            "Abrir su portal", $"https://{notice.Host}/ingresar"),
        DomainStatus.Unreachable => NoticeEmail.Compose(
            to, brand, $"Su dominio {notice.Host} dejó de responder",
            [
                $"Varias revisiones seguidas del DNS de «{notice.Host}» fallaron: el portal dejó de mostrar su marca en ese dominio y su certificado no se renueva.",
                "Se recupera solo cuando los registros vuelvan a estar bien. Revíselos en la pantalla Marca, sección Dominio del portal.",
            ],
            "Abrir la pantalla Marca", $"{portal}/revendedor/marca"),
        _ => NoticeEmail.Compose(
            to, brand, $"Se asignó el dominio {notice.Host} a su portal",
            [
                $"La plataforma asignó el dominio «{notice.Host}» al portal de {brand.Name}.",
                "Para activarlo, cree en el DNS de ese dominio los dos registros que indica la pantalla Marca, sección Dominio del portal. Se verifican solos cada pocos minutos.",
            ],
            "Abrir la pantalla Marca", $"{portal}/revendedor/marca"),
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "A notice ({Kind}) could not be queued ({Failure}); the change stands.")]
    private static partial void LogNotQueued(ILogger logger, string kind, string failure);
}
