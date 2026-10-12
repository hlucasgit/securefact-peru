using SecureFact.Notifications;

namespace SecureFact.Unit.Tests.Notifications;

/// <summary>The shape of a notice with the sentences of a reseller (ADR-072).</summary>
public class NoticeEmailTests
{
    private static readonly string[] Facts = ["Su cargo vence el 12/10/2026."];

    [Fact]
    public void The_greeting_opens_and_the_closing_comes_before_the_support_line_and_the_signature()
    {
        var brand = new EmailBrand("Canal", "ayuda@canal.test", "Hola, le escribimos del canal.", "Atendemos de 9 a 18 h.");

        var mail = NoticeEmail.Compose("a@b.test", brand, "Asunto", Facts, "Ver", "https://portal.test/plan");

        var lines = mail.Text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["Hola, le escribimos del canal.", "Su cargo vence el 12/10/2026.", "Atendemos de 9 a 18 h.", "Si necesita ayuda, escriba a ayuda@canal.test."], lines[..4]);
        Assert.EndsWith("Canal · Con tecnología SecureFact", mail.Text, StringComparison.Ordinal);
        Assert.True(mail.Html.IndexOf("Hola, le escribimos", StringComparison.Ordinal) < mail.Html.IndexOf("Su cargo vence", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_sentences_the_notice_is_what_it_always_was_and_the_html_escapes_the_ones_it_has()
    {
        var plain = NoticeEmail.Compose("a@b.test", new EmailBrand("Canal", null), "Asunto", Facts);
        Assert.Equal("Su cargo vence el 12/10/2026.\n\nCanal · Con tecnología SecureFact", plain.Text);

        var escaped = NoticeEmail.Compose("a@b.test", new EmailBrand("Canal", null, "Gracias & hasta pronto", "Con <afecto>"), "Asunto", Facts);
        Assert.Contains("Gracias &amp; hasta pronto", escaped.Html, StringComparison.Ordinal);
        Assert.Contains("Con &lt;afecto&gt;", escaped.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<afecto>", escaped.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_brand_for_the_reseller_itself_has_none()
    {
        var brand = new EmailBrand("Canal", "ayuda@canal.test", "Hola", "Chau").WithoutTexts();

        Assert.Equal((null, null, "Canal", "ayuda@canal.test"), (brand.Greeting, brand.Closing, brand.Name, brand.SupportEmail));
        Assert.DoesNotContain("Hola", NoticeEmail.Compose("a@b.test", brand, "Asunto", Facts).Text, StringComparison.Ordinal);
    }
}
