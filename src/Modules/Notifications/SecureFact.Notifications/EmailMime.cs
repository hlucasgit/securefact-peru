using MimeKit;
using SecureFact.Notifications.Contracts;

namespace SecureFact.Notifications;

internal static class EmailMime
{
    public static MimeMessage Build(EmailMessage message, EmailOptions options)
    {
        try
        {
            var mime = new MimeMessage();
            mime.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(message.FromName) ? options.FromName : message.FromName, options.From ?? throw new EmailDeliveryException("Email:From is not configured.")));
            mime.To.Add(MailboxAddress.Parse(message.To));
            if (!string.IsNullOrWhiteSpace(message.ReplyTo))
            {
                mime.ReplyTo.Add(MailboxAddress.Parse(message.ReplyTo));
            }

            mime.Subject = message.Subject;
            mime.Body = new BodyBuilder { TextBody = message.Text, HtmlBody = message.Html }.ToMessageBody();
            return mime;
        }
        catch (ParseException failure)
        {
            throw new EmailDeliveryException("An address of the e-mail is not valid.", failure);
        }
    }
}
