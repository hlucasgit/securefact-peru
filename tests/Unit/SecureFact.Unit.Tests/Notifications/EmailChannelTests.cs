using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using SecureFact.Notifications;
using SecureFact.Notifications.Contracts;

namespace SecureFact.Unit.Tests.Notifications;

public sealed class EmailChannelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sf-mail-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static IEmailSender Build(EmailOptions options, bool production = false)
    {
        var services = new ServiceCollection().AddNotificationsModule(options, production);
        return services.BuildServiceProvider().GetRequiredService<IEmailSender>();
    }

    [Fact]
    public async Task Without_a_channel_nothing_is_sent_and_the_caller_is_told()
    {
        var sender = Build(new EmailOptions());

        await Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(new EmailMessage("a@b.pe", "s", "t", "<p>t</p>"), CancellationToken.None));
    }

    [Fact]
    public async Task The_sandbox_writes_the_message_as_an_eml_with_the_brand_as_the_sender_name_and_the_reply_address()
    {
        var sender = Build(new EmailOptions { Provider = "Sandbox", From = "no-responder@securefact.test", Sandbox = { Directory = _directory } });

        await sender.SendAsync(new EmailMessage("cliente@ejemplo.pe", "Hola", "texto", "<p>html</p>", "Distribuidora Ñandú", "ayuda@ejemplo.pe"), CancellationToken.None);

        var file = Assert.Single(Directory.GetFiles(_directory, "*.eml"));
        var mime = await MimeMessage.LoadAsync(file);
        Assert.Equal("no-responder@securefact.test", mime.From.Mailboxes.Single().Address);
        Assert.Equal("Distribuidora Ñandú", mime.From.Mailboxes.Single().Name);
        Assert.Equal("ayuda@ejemplo.pe", mime.ReplyTo.Mailboxes.Single().Address);
        Assert.Equal("cliente@ejemplo.pe", mime.To.Mailboxes.Single().Address);
        Assert.Equal("texto", mime.TextBody);
        Assert.Equal("<p>html</p>", mime.HtmlBody);
    }

    [Fact]
    public async Task An_address_that_is_not_valid_is_a_delivery_failure_and_not_a_crash()
    {
        var sender = Build(new EmailOptions { Provider = "Sandbox", From = "no-responder@securefact.test", Sandbox = { Directory = _directory } });

        await Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(new EmailMessage("no es una dirección", "s", "t", "h"), CancellationToken.None));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task An_smtp_server_that_does_not_answer_is_a_delivery_failure_that_does_not_echo_the_message()
    {
        var sender = Build(new EmailOptions { Provider = "Smtp", From = "no-responder@securefact.test", Smtp = { Host = "127.0.0.1", Port = 1, Security = "None", TimeoutSeconds = 2 } });

        var failure = await Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(new EmailMessage("a@b.pe", "s", "secret-token-in-the-body", "h"), CancellationToken.None));

        Assert.DoesNotContain("secret-token-in-the-body", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Other")]
    public void An_unknown_provider_is_refused(string provider) =>
        Assert.Throws<InvalidOperationException>(() => Build(new EmailOptions { Provider = provider }));

    [Fact]
    public void Production_refuses_the_sandbox_and_an_smtp_without_encryption()
    {
        Assert.Throws<InvalidOperationException>(() => Build(new EmailOptions { Provider = "Sandbox", From = "a@b.pe", Sandbox = { Directory = _directory } }, production: true));
        Assert.Throws<InvalidOperationException>(() => Build(new EmailOptions { Provider = "Smtp", From = "a@b.pe", Smtp = { Host = "smtp.ejemplo.pe", Security = "None" } }, production: true));
        Assert.NotNull(Build(new EmailOptions { Provider = "Smtp", From = "a@b.pe", Smtp = { Host = "smtp.ejemplo.pe", Security = "StartTls" } }, production: true));
    }

    [Theory]
    [InlineData("Smtp", null, "a@b.pe", "Email:Smtp:Host")]
    [InlineData("Smtp", "smtp.ejemplo.pe", null, "Email:From")]
    public void A_channel_needs_its_server_and_its_sender_address(string provider, string? host, string? from, string missing)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Build(new EmailOptions { Provider = provider, From = from, Smtp = { Host = host } }));

        Assert.Contains(missing, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_smtp_security_that_does_not_exist_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => Build(new EmailOptions { Provider = "Smtp", From = "a@b.pe", Smtp = { Host = "smtp.ejemplo.pe", Security = "Maybe" } }));
    }

    [Fact]
    public void The_reset_message_escapes_the_name_and_the_link_in_the_html_and_signs_with_the_brand()
    {
        var message = PasswordResetEmail.Compose("u@x.pe", new EmailBrand("Casa <script>", "ayuda@x.pe"), "https://p.x.pe/restablecer#token=a&b", 30);

        Assert.DoesNotContain("<script>", message.Html, StringComparison.Ordinal);
        Assert.Contains("https://p.x.pe/restablecer#token=a&amp;b", message.Html, StringComparison.Ordinal);
        Assert.Contains("https://p.x.pe/restablecer#token=a&b", message.Text, StringComparison.Ordinal);
        Assert.Equal("Casa <script>", message.FromName);
        Assert.Equal("ayuda@x.pe", message.ReplyTo);
        Assert.Contains("ayuda@x.pe", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_platform_brand_has_no_reply_address()
    {
        var message = PasswordResetEmail.Compose("u@x.pe", EmailBrand.Platform, "https://p.x.pe/restablecer#token=t", 5);

        Assert.Null(message.ReplyTo);
        Assert.Equal("SecureFact Perú", message.FromName);
        Assert.Contains("5 minutos", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_warning_of_a_changed_password_escapes_the_brand_and_carries_no_way_in()
    {
        var message = PasswordChangedEmail.Compose("u@x.pe", new EmailBrand("Casa <i>Sol</i>", "ayuda@x.pe"), "https://p.x.pe/recuperar", "07/10/2026 19:05");

        Assert.DoesNotContain("<i>", message.Html, StringComparison.Ordinal);
        Assert.Contains("07/10/2026 19:05", message.Text, StringComparison.Ordinal);
        Assert.Contains("https://p.x.pe/recuperar", message.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("token", message.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ayuda@x.pe", message.ReplyTo);
    }

    [Fact]
    public void A_notice_says_the_same_in_text_and_html_escapes_everything_and_signs_with_the_brand_and_its_support()
    {
        var message = NoticeEmail.Compose("u@x.pe", new EmailBrand("Casa <b>Sol</b>", "ayuda@x.pe"), "Aviso <1>", ["Primero & segundo", "Cuenta «A<B>»"], "Ingresar", "https://p.x.pe/ingresar?a=1&b=2");

        Assert.DoesNotContain("<b>", message.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<B>", message.Html, StringComparison.Ordinal);
        Assert.Contains("Primero &amp; segundo", message.Html, StringComparison.Ordinal);
        Assert.Contains("https://p.x.pe/ingresar?a=1&amp;b=2", message.Html, StringComparison.Ordinal);
        Assert.Contains("Primero & segundo", message.Text, StringComparison.Ordinal);
        Assert.Contains("https://p.x.pe/ingresar?a=1&b=2", message.Text, StringComparison.Ordinal);
        Assert.Contains("Si necesita ayuda, escriba a ayuda@x.pe.", message.Text, StringComparison.Ordinal);
        Assert.EndsWith("Con tecnología SecureFact", message.Text, StringComparison.Ordinal);
        Assert.Equal(("Casa <b>Sol</b>", "ayuda@x.pe", "Aviso <1>"), (message.FromName, message.ReplyTo, message.Subject));
    }

    [Fact]
    public void A_notice_without_link_or_support_has_neither()
    {
        var message = NoticeEmail.Compose("u@x.pe", EmailBrand.Platform, "Aviso", ["Solo texto"]);

        Assert.DoesNotContain("href", message.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("ayuda", message.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(message.ReplyTo);
    }
}
