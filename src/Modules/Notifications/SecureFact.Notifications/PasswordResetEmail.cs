using System.Net;
using SecureFact.Notifications.Contracts;

namespace SecureFact.Notifications;

/// <summary>The brand an e-mail is signed with: the one of the reseller of the account, or the platform's own when there is none (ADR-044, ADR-052).</summary>
public sealed record EmailBrand(string Name, string? SupportEmail)
{
    public static EmailBrand Platform { get; } = new("SecureFact Perú", null);
}

/// <summary>
/// The e-mail with the link to choose a new password. The token goes in the fragment of the link (after the #), which a browser never sends to a server: it does not reach the logs of
/// the edge or of a proxy, nor a Referer header (ADR-052).
/// </summary>
public static class PasswordResetEmail
{
    public static EmailMessage Compose(string to, EmailBrand brand, string resetLink, int validMinutes)
    {
        ArgumentNullException.ThrowIfNull(brand);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(resetLink);

        var subject = $"Recupere su contraseña de {brand.Name}";
        var support = brand.SupportEmail is { Length: > 0 } address ? $"Si necesita ayuda, escriba a {address}." : string.Empty;

        var text = $"""
            Pidieron restablecer la contraseña de su cuenta en {brand.Name}.

            Para elegir una contraseña nueva, abra este enlace (vale {validMinutes} minutos y se usa una sola vez):
            {resetLink}

            Si usted no lo pidió, ignore este mensaje: su contraseña no cambia.
            {support}

            {brand.Name} · Con tecnología SecureFact
            """;

        var supportHtml = support.Length == 0 ? string.Empty : $"<p>{WebUtility.HtmlEncode(support)}</p>";
        var html = $"""
            <!doctype html>
            <html lang="es"><body style="font-family:Arial,Helvetica,sans-serif;color:#1a1a1a">
            <p>Pidieron restablecer la contraseña de su cuenta en <strong>{WebUtility.HtmlEncode(brand.Name)}</strong>.</p>
            <p>Para elegir una contraseña nueva, abra este enlace (vale {validMinutes} minutos y se usa una sola vez):</p>
            <p><a href="{WebUtility.HtmlEncode(resetLink)}">{WebUtility.HtmlEncode(resetLink)}</a></p>
            <p>Si usted no lo pidió, ignore este mensaje: su contraseña no cambia.</p>
            {supportHtml}
            <p style="color:#555;font-size:12px">{WebUtility.HtmlEncode(brand.Name)} · Con tecnología SecureFact</p>
            </body></html>
            """;

        return new EmailMessage(to, subject, text, html, brand.Name, brand.SupportEmail);
    }
}
