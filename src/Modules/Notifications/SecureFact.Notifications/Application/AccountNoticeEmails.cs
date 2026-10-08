using Microsoft.Extensions.Logging;
using SecureFact.Identity.Contracts;
using SecureFact.Notifications.Contracts;

namespace SecureFact.Notifications.Application;

/// <summary>Tells a person that an account exists for them (ADR-054). It carries no password: whoever created the account gave it, and the person can choose another with «¿Olvidó su contraseña?».</summary>
internal sealed partial class AccountNoticeEmails(NoticeContext context, IEmailOutbox queue, ILogger<AccountNoticeEmails> logger) : IAccountNotices
{
    public async Task AccountCreatedAsync(AccountCreatedNotice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        try
        {
            var (brand, portal, _) = await context.ForAsync(notice.TenantId, notice.ResellerId, cancellationToken);
            await queue.EnqueueAsync(
                NoticeEmail.Compose(
                    notice.Email, brand, $"Se creó su cuenta en {brand.Name}",
                    [
                        $"Hola {notice.DisplayName}: se creó una cuenta para usted en {brand.Name}.",
                        "Ingrese con este mismo correo y la contraseña que le dio quien creó la cuenta. Si no la tiene o la olvidó, elija una nueva con «¿Olvidó su contraseña?».",
                    ],
                    "Ingresar", $"{portal}/ingresar"),
                cancellationToken: cancellationToken);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogNotQueued(logger, failure.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The notice of a new account could not be queued ({Failure}); the account stands.")]
    private static partial void LogNotQueued(ILogger logger, string failure);
}
