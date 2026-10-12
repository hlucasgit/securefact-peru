using System.Globalization;
using Microsoft.Extensions.Logging;
using SecureFact.Identity.Contracts;
using SecureFact.Notifications.Contracts;

namespace SecureFact.Notifications.Application;

/// <summary>
/// The collection notices (ADR-068): a charge was issued, it falls due, it is overdue, the account is near its suspension, a payment arrived. They go to the active owners of the account, each fact
/// once per address (the dedupe key), and, like every notice, they are best-effort: the charge or the payment that raised them stands whatever happens here.
/// </summary>
internal sealed partial class BillingNoticeEmails(IAccountDirectory directory, NoticeContext context, IEmailOutbox queue, ILogger<BillingNoticeEmails> logger) : IBillingNotices
{
    private const int MaxLines = 30;

    private static readonly string[] Months = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    public async Task<bool> SendAsync(BillingNotice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (!Enum.IsDefined(notice.Kind))
        {
            return false;
        }

        try
        {
            var owners = await directory.TenantOwnerEmailsAsync(notice.TenantId, cancellationToken);
            if (owners.Count == 0)
            {
                return false;
            }

            var (brand, portal, _) = await context.ForAsync(notice.TenantId, null, cancellationToken);
            var key = notice.Kind == BillingNoticeKind.PaymentReceived ? $"billing:payment:{notice.PaymentId:N}" : $"billing:{(int)notice.Kind}:{notice.ChargeId:N}";
            var queued = false;
            foreach (var owner in owners)
            {
                queued |= await queue.EnqueueAsync(Compose(owner, brand, portal, notice), key, cancellationToken);
            }

            return queued;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogNotQueued(logger, notice.Kind.ToString(), failure.GetType().Name);
            return false;
        }
    }

    public async Task<bool> ResellerDigestAsync(ResellerCollectionDigest digest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(digest);
        if (digest.Lines.Count == 0 && digest.SuspendedTenants.Count == 0)
        {
            return false;
        }

        return await SendToResellerAsync(
            digest.ResellerId, $"billing:reseller-digest:{digest.ResellerId:N}:{digest.Day:yyyyMMdd}", "reseller digest",
            (to, brand, portal) =>
            {
                var paragraphs = new List<string> { "Estos clientes suyos tienen cobros atrasados con la plataforma. Cada uno ya recibió el aviso por correo; usted puede ayudarlo a regularizar antes de que se suspenda su cuenta." };
                paragraphs.AddRange(digest.Lines.Take(MaxLines).Select(line =>
                    $"• {line.TenantName}: cargo de {MonthName(line.Period)} vencido el {Date(line.DueOn)}, saldo {Money(line.Currency, line.Balance)}"
                    + (line.SuspendOn is { } on ? (line.Kind == BillingNoticeKind.SuspensionNear ? $". Se suspende el {Date(on)}." : $". Se suspende el {Date(on)} si no paga.") : ".")));
                if (digest.Lines.Count > MaxLines)
                {
                    paragraphs.Add($"… y {digest.Lines.Count - MaxLines} más: véalos en su lista de cuentas.");
                }

                if (digest.SuspendedTenants.Count > 0)
                {
                    paragraphs.Add($"Se suspendieron hoy por falta de pago: {string.Join(", ", digest.SuspendedTenants.Take(MaxLines))}{(digest.SuspendedTenants.Count > MaxLines ? " y otros" : string.Empty)}. Se reactivan solas cuando regularizan.");
                }

                var count = digest.Lines.Count + digest.SuspendedTenants.Count;
                return NoticeEmail.Compose(to, brand, count == 1 ? "Un cliente suyo tiene un cobro atrasado" : $"{count} clientes suyos tienen cobros atrasados", paragraphs, "Ver mis cuentas", $"{portal}/revendedor/cuentas");
            },
            cancellationToken);
    }

    public async Task<bool> CommissionSettledAsync(CommissionSettledNotice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return await SendToResellerAsync(
            notice.ResellerId, $"billing:settlement:{notice.SettlementId:N}", "commission settlement",
            (to, brand, portal) => NoticeEmail.Compose(
                to, brand, $"Liquidaron sus comisiones de {MonthName(notice.Month)}",
                [
                    $"La plataforma liquidó sus comisiones de {MonthName(notice.Month)}: {Money("PEN", notice.Total)} por {notice.Entries} {(notice.Entries == 1 ? "pago" : "pagos")} de sus clientes, con fecha {Date(notice.SettledOn)}.",
                    notice.Reference is { Length: > 0 } reference ? $"Referencia: {reference}" : string.Empty,
                ],
                "Ver mis comisiones", $"{portal}/revendedor/comisiones"),
            cancellationToken);
    }

    private async Task<bool> SendToResellerAsync(Guid resellerId, string dedupeKey, string kind, Func<string, EmailBrand, string, EmailMessage> compose, CancellationToken cancellationToken)
    {
        try
        {
            var admins = await directory.ResellerAdminEmailsAsync(resellerId, cancellationToken);
            if (admins.Count == 0)
            {
                return false;
            }

            var (resellerBrand, portal, _) = await context.ForAsync(null, resellerId, cancellationToken);
            var brand = resellerBrand.WithoutTexts();
            var queued = false;
            foreach (var admin in admins)
            {
                queued |= await queue.EnqueueAsync(compose(admin, brand, portal), dedupeKey, cancellationToken);
            }

            return queued;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogNotQueued(logger, kind, failure.GetType().Name);
            return false;
        }
    }

    private static EmailMessage Compose(string to, EmailBrand brand, string portal, BillingNotice notice)
    {
        var month = MonthName(notice.Period);
        var total = Money(notice.Currency, notice.Total);
        var balance = Money(notice.Currency, notice.Balance);
        var due = Date(notice.DueOn);
        var suspension = notice.SuspendOn is { } on
            ? $"Si el cargo sigue sin pagarse, la cuenta se suspende el {Date(on)}: nadie podrá ingresar ni emitir comprobantes hasta que se regularice."
            : string.Empty;
        const string Label = "Ver mi plan y mis cargos";
        var link = $"{portal}/plan";

        return notice.Kind switch
        {
            BillingNoticeKind.ChargeIssued => NoticeEmail.Compose(
                to, brand, $"Cargo de {month} por {total}: vence el {due}",
                [$"Se emitió el cargo del servicio de {month} por {total}. Vence el {due}.", suspension],
                Label, link),
            BillingNoticeKind.DueSoon => NoticeEmail.Compose(
                to, brand, $"Su cargo de {month} vence el {due}",
                [$"El cargo del servicio de {month} vence el {due}. Saldo pendiente: {balance}.", suspension],
                Label, link),
            BillingNoticeKind.Overdue => NoticeEmail.Compose(
                to, brand, $"Su cargo de {month} está vencido",
                [$"El cargo del servicio de {month} venció el {due}. Saldo pendiente: {balance}.", suspension, "Si ya pagó, escriba a quien le brinda el servicio para que registre el pago."],
                Label, link),
            BillingNoticeKind.SuspensionNear => NoticeEmail.Compose(
                to, brand, $"Su cuenta se suspende el {Date(notice.SuspendOn ?? notice.DueOn)} si no paga",
                [$"El cargo del servicio de {month} venció el {due} y sigue sin pagarse. Saldo pendiente: {balance}.", suspension, "Si ya pagó, escriba a quien le brinda el servicio para que registre el pago."],
                Label, link),
            _ => NoticeEmail.Compose(
                to, brand, $"Recibimos su pago de {Money(notice.Currency, notice.PaymentAmount ?? 0m)}",
                [
                    $"Se registró un pago de {Money(notice.Currency, notice.PaymentAmount ?? 0m)} al cargo del servicio de {month}.",
                    notice.Balance <= 0m ? "El cargo quedó pagado." : $"Saldo pendiente del cargo: {balance}, que vence el {due}.",
                ],
                Label, link),
        };
    }

    private static string Money(string currency, decimal amount) =>
        $"{(currency == "PEN" ? "S/" : currency)} {amount.ToString("0.00", CultureInfo.InvariantCulture)}";

    private static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>«2026-09» as «septiembre de 2026»; anything else is shown as it came.</summary>
    private static string MonthName(string period) =>
        period.Length == 7 && period[4] == '-' && int.TryParse(period[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year) && int.TryParse(period[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var month) && month is >= 1 and <= 12
            ? $"{Months[month - 1]} de {year}"
            : period;

    [LoggerMessage(Level = LogLevel.Warning, Message = "A collection notice ({Kind}) could not be queued ({Failure}); the work stands.")]
    private static partial void LogNotQueued(ILogger logger, string kind, string failure);
}
