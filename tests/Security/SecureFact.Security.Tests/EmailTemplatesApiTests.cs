using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecureFact.Identity.Contracts;
using SecureFact.Notifications.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>The sentences that a reseller adds to the e-mails of its brand (ADR-072): where they go, where they do not, and what they cannot say.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class EmailTemplatesApiTests(ApiFixture api)
{
    private const string Greeting = "Gracias por confiar en nosotros & en nuestro equipo.";
    private const string Closing = "Atendemos de lunes a viernes de 9 a 18 h, al 01 555 0100.";

    private sealed record Row(Guid Id, string Name);

    private sealed record Settings(string? BrandName, string? EmailGreeting, string? EmailClosing);

    private sealed record Setup(HttpClient Reseller, Guid ResellerId, string ResellerAdminEmail, Guid TenantId, string OwnerEmail);

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private static Task<HttpResponseMessage> SetBrandAsync(HttpClient client, string? name = "Distribuidora Textos", string? greeting = Greeting, string? closing = Closing, string path = "/api/v1/reseller/branding") =>
        client.PutAsJsonAsync(path, new { brandName = name, primaryColor = "#0b5394", supportEmail = "ayuda@textos.test", emailGreeting = greeting, emailClosing = closing });

    private async Task<Setup> NewSetupAsync(HttpClient admin, string? greeting = Greeting, string? closing = Closing)
    {
        var created = await admin.PostAsJsonAsync("/api/v1/platform/resellers", new { name = $"Textos {Guid.NewGuid():N}"[..18] });
        var reseller = (await created.Content.ReadFromJsonAsync<Row>(ApiFixture.JsonOptions))!;
        var adminEmail = $"{Guid.NewGuid():N}@reseller.test";
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/users", new { email = adminEmail, displayName = "Admin", password = ApiFixture.StrongPassword, roles = new[] { Roles.ResellerAdmin }, resellerId = reseller.Id })).StatusCode);
        var client = api.ClientFor(await api.LoginOkAsync(adminEmail, ApiFixture.StrongPassword));
        Assert.Equal(HttpStatusCode.OK, (await SetBrandAsync(client, greeting: greeting, closing: closing)).StatusCode);
        var opened = await client.PostAsJsonAsync("/api/v1/reseller/tenants", new { name = $"Cliente {Guid.NewGuid():N}"[..20], environment = "Sandbox" });
        var tenant = (await opened.Content.ReadFromJsonAsync<Row>(ApiFixture.JsonOptions))!;
        var ownerEmail = $"{Guid.NewGuid():N}@cliente.test";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/v1/reseller/tenants/{tenant.Id}/owner", new { email = ownerEmail, displayName = "Dueño Uno", password = ApiFixture.StrongPassword })).StatusCode);
        return new Setup(client, reseller.Id, adminEmail, tenant.Id, ownerEmail);
    }

    private async Task<EmailMessage> SingleMailAsync(string email, Predicate<EmailMessage>? which = null)
    {
        await api.DrainMailAsync();
        return Assert.Single(api.Mail.To(email), which ?? (_ => true));
    }

    [Fact]
    public async Task The_customers_of_the_reseller_get_its_sentences_around_what_the_notice_says_and_before_the_support_line()
    {
        using var admin = await api.AdminClientAsync();
        var setup = await NewSetupAsync(admin);

        var welcome = await SingleMailAsync(setup.OwnerEmail);

        var text = welcome.Text;
        var greeting = text.IndexOf(Greeting, StringComparison.Ordinal);
        var fact = text.IndexOf("se creó una cuenta para usted", StringComparison.Ordinal);
        var closing = text.IndexOf(Closing, StringComparison.Ordinal);
        var support = text.IndexOf("Si necesita ayuda, escriba a ayuda@textos.test", StringComparison.Ordinal);
        Assert.True(greeting == 0 && greeting < fact && fact < closing && closing < support, $"order: {greeting} {fact} {closing} {support}");
        Assert.Contains("Gracias por confiar en nosotros &amp; en nuestro equipo.", welcome.Html, StringComparison.Ordinal); // the HTML says the same, escaped
        Assert.DoesNotContain("Gracias por confiar en nosotros & en", welcome.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_account_that_has_no_reseller_and_the_customers_of_another_reseller_do_not_get_them()
    {
        using var admin = await api.AdminClientAsync();
        var one = await NewSetupAsync(admin);
        var other = await NewSetupAsync(admin, greeting: "Saludo del otro canal", closing: null);
        var directTenant = await api.CreateTenantAsync($"Directa {Guid.NewGuid():N}"[..20]);
        var direct = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, directTenant);

        var mine = await SingleMailAsync(one.OwnerEmail);
        var theirs = await SingleMailAsync(other.OwnerEmail);
        var platform = await SingleMailAsync(direct.Email);

        Assert.DoesNotContain("Saludo del otro canal", mine.Text, StringComparison.Ordinal);
        Assert.Contains("Saludo del otro canal", theirs.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Closing, theirs.Text, StringComparison.Ordinal); // it set none
        Assert.DoesNotContain(Greeting, platform.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Saludo del otro canal", platform.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_e_mails_that_go_to_the_reseller_itself_and_the_one_with_the_link_to_a_new_password_never_carry_them()
    {
        using var admin = await api.AdminClientAsync();
        var setup = await NewSetupAsync(admin);

        var host = $"textos-{Guid.NewGuid():N}"[..20] + ".e2e.test";
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{setup.ResellerId}/host", new { host })).StatusCode);
        var domain = await SingleMailAsync(setup.ResellerAdminEmail, m => m.Subject.Contains("Se asignó el dominio", StringComparison.Ordinal));
        Assert.DoesNotContain(Greeting, domain.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Closing, domain.Text, StringComparison.Ordinal);

        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = setup.OwnerEmail })).StatusCode);
        var reset = await SingleMailAsync(setup.OwnerEmail, m => m.Subject.Contains("Recupere su contraseña", StringComparison.Ordinal));
        Assert.DoesNotContain(Greeting, reset.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Closing, reset.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_texts_are_stored_with_the_brand_edited_by_the_platform_too_and_leave_with_it()
    {
        using var admin = await api.AdminClientAsync();
        var setup = await NewSetupAsync(admin);

        var stored = (await setup.Reseller.GetFromJsonAsync<Settings>("/api/v1/reseller/branding", ApiFixture.JsonOptions))!;
        Assert.Equal((Greeting, Closing), (stored.EmailGreeting, stored.EmailClosing));

        Assert.Equal(HttpStatusCode.OK, (await SetBrandAsync(admin, greeting: "  Hola desde la plataforma  ", closing: "   ", path: $"/api/v1/platform/resellers/{setup.ResellerId}/branding")).StatusCode);
        var edited = (await setup.Reseller.GetFromJsonAsync<Settings>("/api/v1/reseller/branding", ApiFixture.JsonOptions))!;
        Assert.Equal(("Hola desde la plataforma", (string?)null), (edited.EmailGreeting, edited.EmailClosing)); // trimmed; blank is none

        // Without a brand there are no texts: they would be shown under no name.
        Assert.Equal(HttpStatusCode.OK, (await SetBrandAsync(setup.Reseller, name: null)).StatusCode);
        var removed = (await setup.Reseller.GetFromJsonAsync<Settings>("/api/v1/reseller/branding", ApiFixture.JsonOptions))!;
        Assert.Equal(((string?)null, (string?)null, (string?)null), (removed.BrandName, removed.EmailGreeting, removed.EmailClosing));

        // The audit has them, as every other change of the brand.
        var events = (await admin.GetFromJsonAsync<List<JsonElement>>("/api/v1/audit?action=tenancy.reseller.branding_updated&take=50", ApiFixture.JsonOptions))!;
        Assert.Contains(events, e => e.GetProperty("newValues").GetString()!.Contains("Hola desde la plataforma", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Visite https://ofertas.example para más")]
    [InlineData("Escriba a ventas.com ahora")]
    [InlineData("Entre a www.canal.pe")]
    [InlineData("Escríbanos a ventas@canal.test")]
    [InlineData("<b>Gracias</b>")]
    [InlineData("Una línea\nY otra")]
    public async Task A_sentence_with_a_web_address_a_mail_address_markup_or_more_than_one_line_is_refused(string text)
    {
        using var admin = await api.AdminClientAsync();
        var setup = await NewSetupAsync(admin);

        foreach (var attempt in new[] { await SetBrandAsync(setup.Reseller, greeting: text), await SetBrandAsync(setup.Reseller, closing: text) })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, attempt.StatusCode);
            Assert.Equal("SF-BRAND-001", await CodeAsync(attempt));
        }

        var kept = (await setup.Reseller.GetFromJsonAsync<Settings>("/api/v1/reseller/branding", ApiFixture.JsonOptions))!;
        Assert.Equal((Greeting, Closing), (kept.EmailGreeting, kept.EmailClosing)); // a refused change changes nothing
    }

    [Fact]
    public async Task The_sentences_have_a_limit_of_length_and_a_phone_number_and_the_hours_are_fine()
    {
        using var admin = await api.AdminClientAsync();
        var setup = await NewSetupAsync(admin);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetBrandAsync(setup.Reseller, greeting: new string('a', 201))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetBrandAsync(setup.Reseller, closing: new string('a', 401))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetBrandAsync(setup.Reseller, greeting: new string('a', 200), closing: new string('b', 400))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetBrandAsync(setup.Reseller, closing: "Llámenos al +51 1 555 0100, de lunes a viernes de 9:00 a 18:00 h (hora de Lima).")).StatusCode);
    }

    [Fact]
    public async Task Only_the_platform_or_the_reseller_itself_edit_them()
    {
        using var admin = await api.AdminClientAsync();
        var one = await NewSetupAsync(admin);
        var other = await NewSetupAsync(admin);
        var owner = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, one.TenantId);
        using var customer = api.ClientFor(await api.LoginOkAsync(owner.Email, owner.Password));

        Assert.Equal(HttpStatusCode.Forbidden, (await SetBrandAsync(customer, greeting: "Intruso")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetBrandAsync(other.Reseller, greeting: "Intruso", path: $"/api/v1/platform/resellers/{one.ResellerId}/branding")).StatusCode);
        var untouched = (await one.Reseller.GetFromJsonAsync<Settings>("/api/v1/reseller/branding", ApiFixture.JsonOptions))!;
        Assert.Equal(Greeting, untouched.EmailGreeting);
    }
}
