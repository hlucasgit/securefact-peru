using System.Net;
using System.Text;
using SecureFact.Notifications.Contracts;

namespace SecureFact.Notifications;

/// <summary>The shape of every notice (ADR-054): a subject, a few paragraphs, an optional link, and the signature. Text and HTML say the same; the HTML escapes everything it is given.</summary>
public static class NoticeEmail
{
    public static EmailMessage Compose(string to, EmailBrand brand, string subject, IReadOnlyList<string> paragraphs, string? linkLabel = null, string? linkUrl = null)
    {
        ArgumentNullException.ThrowIfNull(brand);
        ArgumentNullException.ThrowIfNull(paragraphs);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var text = new StringBuilder();
        var html = new StringBuilder("<!doctype html>\n<html lang=\"es\"><body style=\"font-family:Arial,Helvetica,sans-serif;color:#1a1a1a\">\n");
        foreach (var paragraph in paragraphs.Append(brand.SupportEmail is { Length: > 0 } support ? $"Si necesita ayuda, escriba a {support}." : string.Empty).Where(p => p.Length > 0))
        {
            text.Append(paragraph).Append("\n\n");
            html.Append("<p>").Append(WebUtility.HtmlEncode(paragraph)).Append("</p>\n");
        }

        if (linkLabel is not null && linkUrl is not null)
        {
            text.Append(linkLabel).Append(":\n").Append(linkUrl).Append("\n\n");
            html.Append("<p><a href=\"").Append(WebUtility.HtmlEncode(linkUrl)).Append("\">").Append(WebUtility.HtmlEncode(linkLabel)).Append("</a></p>\n");
        }

        var signature = $"{brand.Name} · Con tecnología SecureFact";
        text.Append(signature);
        html.Append("<p style=\"color:#555;font-size:12px\">").Append(WebUtility.HtmlEncode(signature)).Append("</p>\n</body></html>");
        return new EmailMessage(to, subject, text.ToString(), html.ToString(), brand.Name, brand.SupportEmail);
    }
}
