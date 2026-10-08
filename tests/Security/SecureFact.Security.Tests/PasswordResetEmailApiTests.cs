using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecureFact.Identity.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>The e-mail with the link to choose a new password (ADR-052): who it says it is from, where it points, and that a failed delivery neither shows nor leaks anything.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class PasswordResetEmailApiTests(ApiFixture api)
{
    private sealed record Row(Guid Id, string Name, bool IsActive);

    private sealed record Created(Guid Id);

    private async Task<(Guid Id, string AdminEmail, HttpClient Client)> NewBrandedResellerAsync(HttpClient admin, string brand, bool verifiedHost, string? host = null)
    {
        var created = await admin.PostAsJsonAsync("/api/v1/platform/resellers", new { name = $"Correo {Guid.NewGuid():N}"[..18] });
        var row = (await created.Content.ReadFromJsonAsync<Row>(ApiFixture.JsonOptions))!;
        var email = $"{Guid.NewGuid():N}@reseller.test";
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "Admin", password = ApiFixture.StrongPassword, roles = new[] { Roles.ResellerAdmin }, resellerId = row.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{row.Id}/branding", new { brandName = brand, primaryColor = "#0b5394", supportEmail = "ayuda@marca.test" })).StatusCode);
        if (host is not null)
        {
            var set = await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{row.Id}/host", new { host });
            using var body = JsonDocument.Parse(await set.Content.ReadAsStringAsync());
            if (verifiedHost)
            {
                api.Dns.PublishTxt(body.RootElement.GetProperty("txtName").GetString()!, body.RootElement.GetProperty("txtValue").GetString()!);
                api.Dns.PointAt(host, FakeDomainNameSystem.EdgeHost);
                Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/platform/resellers/{row.Id}/domain/verify", null)).StatusCode);
            }
        }

        return (row.Id, email, api.ClientFor(await api.LoginOkAsync(email, ApiFixture.StrongPassword)));
    }

    private async Task RequestResetAsync(string email)
    {
        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email })).StatusCode);
    }

    [Fact]
    public async Task A_platform_user_gets_the_link_of_the_platform_with_the_token_in_the_fragment()
    {
        using var admin = await api.AdminClientAsync();
        var tenantId = await api.CreateTenantAsync($"Correo plataforma {Guid.NewGuid():N}"[..28]);
        var owner = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);

        await RequestResetAsync(owner.Email);

        var mail = Assert.Single(api.Mail.To(owner.Email));
        var token = api.Notifier.TokenFor(owner.Email)!;
        Assert.Contains($"{ApiFixture.PublicUrl}/restablecer#token=", mail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain($"?token={token}", mail.Text, StringComparison.Ordinal); // never in the query: a server would see it
        Assert.Equal("SecureFact Perú", mail.FromName);
        Assert.Null(mail.ReplyTo);
        Assert.Contains("SecureFact Perú", mail.Subject, StringComparison.Ordinal);
        Assert.Contains("30 minutos", mail.Text, StringComparison.Ordinal);
        Assert.Contains(token, mail.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_address_that_does_not_exist_gets_nothing()
    {
        var ghost = $"{Guid.NewGuid():N}@nadie.test";

        await RequestResetAsync(ghost);

        Assert.Empty(api.Mail.To(ghost));
    }

    [Fact]
    public async Task The_users_of_a_reseller_get_its_brand_and_its_verified_portal_and_answers_go_to_its_support()
    {
        using var admin = await api.AdminClientAsync();
        var host = $"correo-{Guid.NewGuid():N}"[..20] + ".e2e.test";
        var reseller = await NewBrandedResellerAsync(admin, "Distribuidora Andina", verifiedHost: true, host);

        // The reseller's own user, and the owner of a tenant it opened: both belong to its portal.
        var customer = await reseller.Client.PostAsJsonAsync("/api/v1/reseller/tenants", new { name = $"Cliente {Guid.NewGuid():N}"[..20], environment = "Sandbox" });
        var tenantId = (await customer.Content.ReadFromJsonAsync<Created>(ApiFixture.JsonOptions))!.Id;
        var ownerEmail = $"{Guid.NewGuid():N}@cliente.test";
        Assert.Equal(HttpStatusCode.Created, (await reseller.Client.PostAsJsonAsync($"/api/v1/reseller/tenants/{tenantId}/owner", new { email = ownerEmail, password = ApiFixture.StrongPassword, displayName = "Dueño" })).StatusCode);

        foreach (var email in new[] { reseller.AdminEmail, ownerEmail })
        {
            await RequestResetAsync(email);
            var mail = Assert.Single(api.Mail.To(email));
            Assert.Contains($"https://{host}/restablecer#token=", mail.Text, StringComparison.Ordinal);
            Assert.Equal("Distribuidora Andina", mail.FromName);
            Assert.Equal("ayuda@marca.test", mail.ReplyTo);
            Assert.Contains("Distribuidora Andina", mail.Subject, StringComparison.Ordinal);
            Assert.DoesNotContain(ApiFixture.PublicUrl, mail.Text, StringComparison.Ordinal);
            Assert.Contains("Con tecnología SecureFact", mail.Text, StringComparison.Ordinal); // the platform is not hidden
        }
    }

    [Fact]
    public async Task A_domain_that_is_not_verified_does_not_receive_the_link_but_the_brand_still_signs_the_message()
    {
        using var admin = await api.AdminClientAsync();
        var host = $"pendiente-{Guid.NewGuid():N}"[..20] + ".e2e.test";
        var reseller = await NewBrandedResellerAsync(admin, "Marca sin dominio", verifiedHost: false, host);

        await RequestResetAsync(reseller.AdminEmail);

        var mail = Assert.Single(api.Mail.To(reseller.AdminEmail));
        Assert.Contains($"{ApiFixture.PublicUrl}/restablecer#token=", mail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(host, mail.Text, StringComparison.Ordinal);
        Assert.Equal("Marca sin dominio", mail.FromName);
    }

    [Fact]
    public async Task A_name_with_markup_is_escaped_in_the_html_of_the_message()
    {
        using var admin = await api.AdminClientAsync();
        var reseller = await NewBrandedResellerAsync(admin, "Casa <b>&\"Sol\"", verifiedHost: false);

        await RequestResetAsync(reseller.AdminEmail);

        var mail = Assert.Single(api.Mail.To(reseller.AdminEmail));
        Assert.DoesNotContain("<b>", mail.Html, StringComparison.Ordinal);
        Assert.Contains("Casa &lt;b&gt;&amp;&quot;Sol&quot;", mail.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_a_new_password_warns_the_holder_and_a_failed_warning_does_not_undo_the_change()
    {
        using var admin = await api.AdminClientAsync();
        var tenantId = await api.CreateTenantAsync($"Aviso {Guid.NewGuid():N}"[..20]);
        var owner = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        using var anonymous = api.NewClient();

        await RequestResetAsync(owner.Email);
        var first = "A first new passphrase for 2026";
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { token = api.Notifier.TokenFor(owner.Email), newPassword = first })).StatusCode);

        var notice = api.Mail.To(owner.Email).Last();
        Assert.Contains("cambió", notice.Subject, StringComparison.Ordinal);
        Assert.Contains($"{ApiFixture.PublicUrl}/recuperar", notice.Text, StringComparison.Ordinal);
        Assert.Contains("se cerraron todas sus sesiones", notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("#token=", notice.Text, StringComparison.Ordinal); // a warning is not a way in

        // The mail server goes down just before the second change: the password changes all the same.
        await RequestResetAsync(owner.Email);
        var token = api.Notifier.TokenFor(owner.Email);
        var second = "A second new passphrase for 2026";
        api.Mail.Fail = true;
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { token, newPassword = second })).StatusCode);
        }
        finally
        {
            api.Mail.Fail = false;
        }

        Assert.Equal(HttpStatusCode.OK, (await api.LoginAsync(owner.Email, second)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.LoginAsync(owner.Email, first)).StatusCode);
    }

    [Fact]
    public async Task A_failed_delivery_answers_the_same_and_leaves_no_token_in_the_logs()
    {
        using var admin = await api.AdminClientAsync();
        var tenantId = await api.CreateTenantAsync($"Correo caído {Guid.NewGuid():N}"[..26]);
        var owner = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);

        api.Mail.Fail = true;
        try
        {
            await RequestResetAsync(owner.Email); // 204, as for an address that does not exist
        }
        finally
        {
            api.Mail.Fail = false;
        }

        Assert.Empty(api.Mail.To(owner.Email));
        var logs = string.Join(Environment.NewLine, api.Logs.Snapshot());
        Assert.Contains("was not delivered", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("restablecer#token", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(owner.Email, logs.Split('\n').Single(l => l.Contains("was not delivered", StringComparison.Ordinal)), StringComparison.Ordinal);
    }
}
