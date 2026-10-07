using SecureFact.Notifications.Contracts;

namespace SecureFact.Notifications;

/// <summary>Writes each e-mail as an .eml file instead of sending it. For development and the end-to-end tests: it holds one-time tokens, so production refuses it.</summary>
internal sealed class FileEmailSender(EmailOptions options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var mime = EmailMime.Build(message, options);
        var directory = options.Sandbox.Directory ?? throw new EmailDeliveryException("Email:Sandbox:Directory is not configured.");
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.eml");
            await using var stream = File.Create(path);
            await mime.WriteToAsync(stream, cancellationToken);
        }
        catch (IOException failure)
        {
            throw new EmailDeliveryException("The sandbox directory could not be written.", failure);
        }
    }
}
