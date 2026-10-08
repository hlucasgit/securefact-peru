using System.Net;
using SecureFact.Notifications.Contracts;

namespace SecureFact.Notifications;

/// <summary>
/// The warning that the password of the account changed (ADR-052). It carries no secret and no link that signs anyone in: only the time and where to ask for another reset if the
/// change was not the holder's.
/// </summary>
public static class PasswordChangedEmail
{
    public static EmailMessage Compose(string to, EmailBrand brand, string recoverLink, string changedAtLima)
    {
        ArgumentNullException.ThrowIfNull(brand);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoverLink);
        ArgumentException.ThrowIfNullOrWhiteSpace(changedAtLima);

        var subject = $"Su contraseña de {brand.Name} cambió";
        var support = brand.SupportEmail is { Length: > 0 } address ? $"Escriba a {address} si necesita ayuda." : string.Empty;

        var text = $"""
            La contraseña de su cuenta en {brand.Name} cambió el {changedAtLima} (hora de Lima). Por seguridad se cerraron todas sus sesiones.

            Si fue usted, no tiene que hacer nada.

            Si no fue usted, alguien tuvo acceso a su correo. Pida otra contraseña ahora en:
            {recoverLink}
            {support}

            {brand.Name} · Con tecnología SecureFact
            """;

        var supportHtml = support.Length == 0 ? string.Empty : $"<p>{WebUtility.HtmlEncode(support)}</p>";
        var html = $"""
            <!doctype html>
            <html lang="es"><body style="font-family:Arial,Helvetica,sans-serif;color:#1a1a1a">
            <p>La contraseña de su cuenta en <strong>{WebUtility.HtmlEncode(brand.Name)}</strong> cambió el {WebUtility.HtmlEncode(changedAtLima)} (hora de Lima). Por seguridad se cerraron todas sus sesiones.</p>
            <p>Si fue usted, no tiene que hacer nada.</p>
            <p>Si no fue usted, alguien tuvo acceso a su correo. <a href="{WebUtility.HtmlEncode(recoverLink)}">Pida otra contraseña ahora</a>.</p>
            {supportHtml}
            <p style="color:#555;font-size:12px">{WebUtility.HtmlEncode(brand.Name)} · Con tecnología SecureFact</p>
            </body></html>
            """;

        return new EmailMessage(to, subject, text, html, brand.Name, brand.SupportEmail);
    }
}
