using Microsoft.Extensions.Logging;
using SecureFact.Identity.Contracts;
using SecureFact.Notifications.Contracts;

namespace SecureFact.Notifications.Application;

/// <summary>
/// The notices about the business of an account (ADR-055): that it used most of its plan, and that SUNAT rejected a document. They go to the active owners, each fact once per address
/// (the dedupe key), and, like every notice, they are best-effort: the work that raised them stands whatever happens here.
/// </summary>
internal sealed partial class BusinessNoticeEmails(IAccountDirectory directory, NoticeContext context, IEmailOutbox queue, ILogger<BusinessNoticeEmails> logger) : IBusinessNotices
{
    public async Task PlanUsageAsync(PlanUsageNotice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        await SendToOwnersAsync(
            notice.TenantId, $"plan-usage:{notice.TenantId:N}:{notice.Period}:{notice.Percent}", "plan usage",
            (to, brand, portal) => notice.Percent >= 100
                ? NoticeEmail.Compose(
                    to, brand, "Su plan llegó al límite de comprobantes de este mes",
                    [
                        $"Su cuenta usó los {notice.Limit} comprobantes del plan «{notice.PlanName}» de {notice.Period}. Hasta que cambie el mes, no podrá emitir más.",
                        "Para seguir emitiendo antes, pida un plan mayor a quien le brinda el servicio.",
                    ],
                    "Ver su plan y consumo", $"{portal}/plan")
                : NoticeEmail.Compose(
                    to, brand, "Su plan está por llegar al límite de comprobantes de este mes",
                    [
                        $"Su cuenta usó {notice.Used} de los {notice.Limit} comprobantes del plan «{notice.PlanName}» en {notice.Period} ({notice.Percent} %).",
                        "Al llegar al límite no podrá emitir más hasta que cambie el mes. Si lo necesita, pida un plan mayor a quien le brinda el servicio.",
                    ],
                    "Ver su plan y consumo", $"{portal}/plan"),
            cancellationToken);
    }

    public async Task DocumentRejectedAsync(DocumentRejectedNotice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var what = notice.Rejected ? "SUNAT rechazó" : "No se pudo enviar a SUNAT";
        await SendToOwnersAsync(
            notice.TenantId, $"document-problem:{notice.ElectronicDocumentId:N}", "document problem",
            (to, brand, portal) => NoticeEmail.Compose(
                to, brand, $"{what} el comprobante {notice.DocumentName}",
                [
                    notice.Rejected
                        ? $"SUNAT rechazó el comprobante {notice.DocumentName}. Revise la respuesta de SUNAT y corrija el dato indicado antes de volver a emitir."
                        : $"El comprobante {notice.DocumentName} no pudo enviarse a SUNAT después de varios intentos.",
                    notice.Description is { Length: > 0 } description ? $"Respuesta{(notice.ResponseCode is { } code ? $" (código {code})" : string.Empty)}: {description}" : string.Empty,
                ],
                "Ver el comprobante", $"{portal}/documentos"),
            cancellationToken);
    }

    public async Task SupportAccessAsync(SupportAccessNotice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        await SendToOwnersAsync(
            notice.TenantId, $"support-access:{notice.SessionId:N}", "support access",
            (to, brand, portal) => NoticeEmail.Compose(
                to, brand, "Alguien de soporte entró a su cuenta",
                [
                    $"{notice.Who} entró a su cuenta con la autorización que usted dio. Solo puede mirar: no cambia nada. Motivo que indicó: {notice.Reason}",
                    $"Su sesión termina a las {notice.EndsAt.ToOffset(TimeSpan.FromHours(-5)).ToString("HH:mm 'del' dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)} (hora de Lima). Si usted no lo esperaba, quite la autorización ahora.",
                ],
                "Ver y quitar el acceso de soporte", $"{portal}/soporte"),
            cancellationToken);
    }

    public async Task AccountExportedAsync(AccountExportedNotice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        string who;
        try
        {
            who = notice.UserId is { } userId ? await directory.UserNameAsync(notice.TenantId, userId, cancellationToken) ?? "Un propietario de su cuenta" : "Un propietario de su cuenta";
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogNotQueued(logger, "account export", failure.GetType().Name);
            who = "Un propietario de su cuenta";
        }

        await SendToOwnersAsync(
            notice.TenantId, $"account-exported:{notice.ExportId:N}", "account export",
            (to, brand, portal) => NoticeEmail.Compose(
                to, brand, "Se descargaron los datos de su cuenta",
                [
                    $"{who} descargó todos los datos de su cuenta (tablas, comprobantes firmados y constancias de SUNAT) el {notice.At.ToOffset(TimeSpan.FromHours(-5)).ToString("dd/MM/yyyy 'a las' HH:mm", System.Globalization.CultureInfo.InvariantCulture)} (hora de Lima).",
                    "Si usted no lo esperaba, cambie su contraseña y revise los usuarios de la cuenta.",
                ],
                "Ver la auditoría de la cuenta", $"{portal}/auditoria"),
            cancellationToken);
    }

    private async Task SendToOwnersAsync(Guid tenantId, string dedupeKey, string kind, Func<string, EmailBrand, string, EmailMessage> compose, CancellationToken cancellationToken)
    {
        try
        {
            var owners = await directory.TenantOwnerEmailsAsync(tenantId, cancellationToken);
            if (owners.Count == 0)
            {
                return;
            }

            var (brand, portal, _) = await context.ForAsync(tenantId, null, cancellationToken);
            foreach (var owner in owners)
            {
                await queue.EnqueueAsync(compose(owner, brand, portal), dedupeKey, cancellationToken);
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogNotQueued(logger, kind, failure.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A notice ({Kind}) could not be queued ({Failure}); the work stands.")]
    private static partial void LogNotQueued(ILogger logger, string kind, string failure);
}
