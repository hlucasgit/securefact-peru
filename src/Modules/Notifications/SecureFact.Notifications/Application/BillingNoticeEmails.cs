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
