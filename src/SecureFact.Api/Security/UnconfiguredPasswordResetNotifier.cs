using SecureFact.Identity.Contracts;

namespace SecureFact.Api.Security;

/// <summary>
/// Placeholder until the Notifications module exists. It deliberately does not log or store the token (secrets never reach logs);
/// without a real delivery channel a password reset cannot complete, which is the safe failure mode.
/// </summary>
internal sealed partial class UnconfiguredPasswordResetNotifier(ILogger<UnconfiguredPasswordResetNotifier> logger) : IPasswordResetNotifier
{
    public Task SendAsync(string email, string token, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        LogNotConfigured();
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A password reset was requested but no notification channel is configured; the token was discarded.")]
    private partial void LogNotConfigured();
}
