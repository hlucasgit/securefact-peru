using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecureFact.Identity.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>White label (ADR-044): what a reseller sets, what any visitor sees of it and what nobody can do to the brand of another.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class BrandingApiTests(ApiFixture api)
{
    private sealed record Row(Guid Id, string Name, bool IsActive);

    private sealed record Settings(Guid ResellerId, string ResellerName, string? BrandName, string? PrimaryColor, string? SupportEmail, string? Host, string? LogoUrl);

    private sealed record Brand(string BrandName, string PrimaryColor, string? SupportEmail, string? LogoUrl);

    private sealed record Reseller(Row Row, HttpClient Client);

    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] Png(int length = 64, byte fill = 7) => [.. PngHeader, .. Enumerable.Repeat(fill, length - PngHeader.Length)];

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private static string NewHost() => $"portal-{Guid.NewGuid():N}"[..20] + ".e2e.test";

    private async Task<Reseller> NewResellerAsync(HttpClient admin)
    {
        var created = await admin.PostAsJsonAsync("/api/v1/platform/resellers", new { name = $"Marca {Guid.NewGuid():N}"[..18] });
        var row = (await created.Content.ReadFromJsonAsync<Row>(ApiFixture.JsonOptions))!;
        var email = $"{Guid.NewGuid():N}@reseller.test";
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "Admin", password = ApiFixture.StrongPassword, roles = new[] { Roles.ResellerAdmin }, resellerId = row.Id })).StatusCode);
        return new Reseller(row, api.ClientFor(await api.LoginOkAsync(email, ApiFixture.StrongPassword)));
    }

    private static Task<HttpResponseMessage> SetBrandAsync(HttpClient client, string? name = "Distribuidora Andina", string? color = "#0b5394", string? email = "soporte@andina.test", string path = "/api/v1/reseller/branding") =>
        client.PutAsJsonAsync(path, new { brandName = name, primaryColor = color, supportEmail = email });

    private static Task<HttpResponseMessage> SetLogoAsync(HttpClient client, byte[] data, string path = "/api/v1/reseller/branding/logo") =>
        client.PutAsJsonAsync(path, new { dataBase64 = Convert.ToBase64String(data) });

    private async Task<HttpResponseMessage> ForHostAsync(string host)
    {
        using var anonymous = api.NewClient();
        return await anonymous.GetAsync($"/api/v1/branding?host={Uri.EscapeDataString(host)}");
    }

    [Fact]
    public async Task A_reseller_sets_its_brand_and_the_default_look_returns_when_it_removes_it()
    {
        using var admin = await api.AdminClientAsync();
        var reseller = await NewResellerAsync(admin);
        var host = NewHost();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{reseller.Row.Id}/host", new { host })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ForHostAsync(host)).StatusCode); // a reseller with no brand shows the default look

        var saved = await SetBrandAsync(reseller.Client);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var shown = (await (await ForHostAsync(host)).Content.ReadFromJsonAsync<Brand>(ApiFixture.JsonOptions))!;
        Assert.Equal(("Distribuidora Andina", "#0b5394", "soporte@andina.test", null), (shown.BrandName, shown.PrimaryColor, shown.SupportEmail, shown.LogoUrl));

        // Removing the name removes the brand, with its colour and contact.
        Assert.Equal(HttpStatusCode.OK, (await SetBrandAsync(reseller.Client, name: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ForHostAsync(host)).StatusCode);
        var settings = (await reseller.Client.GetFromJsonAsync<Settings>("/api/v1/reseller/branding", ApiFixture.JsonOptions))!;
        Assert.Equal((null, null, null, host), (settings.BrandName, settings.PrimaryColor, settings.SupportEmail, settings.Host));
    }

    [Fact]
    public async Task The_brand_is_validated_and_a_colour_that_makes_the_white_text_unreadable_is_refused()
    {
        using var admin = await api.AdminClientAsync();
        var reseller = await NewResellerAsync(admin);

        foreach (var (name, color, email) in new (string, string, string?)[]
        {
            ("A", "#0b5394", null), (new string('x', 61), "#0b5394", null), ("Con\u0007control", "#0b5394", null),
            ("Marca", "azul", null), ("Marca", "#0b539", null), ("Marca", "#0b5394", "no es un correo"),
        })
        {
            var refused = await SetBrandAsync(reseller.Client, name, color, email);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("SF-BRAND-001", await CodeAsync(refused));
        }

        // Yellow and light grey leave the white text unreadable (contrast below 4.5:1); a dark colour passes.
        foreach (var weak in new[] { "#ffff00", "#cccccc", "#7a7a7a" })
        {
            var refused = await SetBrandAsync(reseller.Client, color: weak);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains("contraste", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.OK, (await SetBrandAsync(reseller.Client, color: "#595959")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetBrandAsync(reseller.Client, color: "#0B5394")).StatusCode);
        Assert.Equal("#0b5394", (await (await reseller.Client.GetAsync("/api/v1/reseller/branding")).Content.ReadFromJsonAsync<Settings>(ApiFixture.JsonOptions))!.PrimaryColor); // stored in lower case
    }

    [Fact]
    public async Task The_logo_is_checked_by_its_content_and_is_served_to_anyone_with_its_version()
    {
        using var admin = await api.AdminClientAsync();
        var reseller = await NewResellerAsync(admin);
        var host = NewHost();
        await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{reseller.Row.Id}/host", new { host });
        await SetBrandAsync(reseller.Client);

        foreach (var (what, data) in new (string, byte[])[]
        {
            ("text that says it is an image", "GIF89a<script>alert(1)</script>"u8.ToArray()),
            ("an SVG, which can carry script", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray()),
            ("an empty file", []),
            ("a file over 200 KB", Png(200 * 1024 + 1)),
        })
        {
            var refused = await SetLogoAsync(reseller.Client, data);
            Assert.True(refused.StatusCode == HttpStatusCode.UnprocessableEntity, what);
            Assert.Equal("SF-BRAND-002", await CodeAsync(refused));
        }

        Assert.Equal("SF-BRAND-002", await CodeAsync(await reseller.Client.PutAsJsonAsync("/api/v1/reseller/branding/logo", new { dataBase64 = "esto no es base64!!" })));

        Assert.Equal(HttpStatusCode.OK, (await SetLogoAsync(reseller.Client, Png())).StatusCode);
        var brand = (await (await ForHostAsync(host)).Content.ReadFromJsonAsync<Brand>(ApiFixture.JsonOptions))!;
        Assert.Matches($"^/api/v1/branding/logos/{reseller.Row.Id}\\?v=[0-9a-f]{{12}}$", brand.LogoUrl);

        using var anonymous = api.NewClient();
        var logo = await anonymous.GetAsync(brand.LogoUrl);
        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Equal("image/png", logo.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png(), await logo.Content.ReadAsByteArrayAsync());
        Assert.NotNull(logo.Headers.ETag);
        Assert.Contains("max-age", logo.Headers.CacheControl!.ToString(), StringComparison.Ordinal);

        // A different logo has a different version, so a cached one is never shown after the change.
        await SetLogoAsync(reseller.Client, Png(fill: 9));
        var changed = (await (await ForHostAsync(host)).Content.ReadFromJsonAsync<Brand>(ApiFixture.JsonOptions))!;
        Assert.NotEqual(brand.LogoUrl, changed.LogoUrl);

        Assert.Equal(HttpStatusCode.OK, (await reseller.Client.DeleteAsync("/api/v1/reseller/branding/logo")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(changed.LogoUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/branding/logos/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task The_domain_is_assigned_by_the_platform_alone_and_is_unique_and_matches_without_case_or_port()
    {
        using var admin = await api.AdminClientAsync();
        var one = await NewResellerAsync(admin);
        var two = await NewResellerAsync(admin);
        var host = NewHost();
        await SetBrandAsync(one.Client);
        await SetBrandAsync(two.Client, name: "Otra marca");

        // A reseller cannot point a domain at itself: that would let it take over the name of another.
        Assert.Equal(HttpStatusCode.Forbidden, (await one.Client.PutAsJsonAsync($"/api/v1/platform/resellers/{one.Row.Id}/host", new { host })).StatusCode);

        foreach (var bad in new[] { "http://portal.ejemplo.pe", "10.0.0.1", "sin-punto", "con espacio.pe", "-malo.pe", "a..pe", "portal.ejemplo.pe/ruta" })
        {
            var refused = await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{one.Row.Id}/host", new { host = bad });
            Assert.True(refused.StatusCode == HttpStatusCode.UnprocessableEntity, bad);
            Assert.Equal("SF-BRAND-001", await CodeAsync(refused));
        }

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{one.Row.Id}/host", new { host = host.ToUpperInvariant() })).StatusCode);
        var taken = await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{two.Row.Id}/host", new { host });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("SF-BRAND-003", await CodeAsync(taken));

        foreach (var spelled in new[] { host, host.ToUpperInvariant(), $"{host}:5173", $"{host}." })
        {
            Assert.Equal("Distribuidora Andina", (await (await ForHostAsync(spelled)).Content.ReadFromJsonAsync<Brand>(ApiFixture.JsonOptions))!.BrandName);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await ForHostAsync("desconocido.e2e.test")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ForHostAsync(string.Empty)).StatusCode);

        // Clearing the domain frees it for another reseller.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{one.Row.Id}/host", new { host = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{two.Row.Id}/host", new { host })).StatusCode);
        Assert.Equal("Otra marca", (await (await ForHostAsync(host)).Content.ReadFromJsonAsync<Brand>(ApiFixture.JsonOptions))!.BrandName);
    }

    [Fact]
    public async Task Nobody_edits_the_brand_of_another_reseller_and_only_the_right_roles_edit_at_all()
    {
        using var admin = await api.AdminClientAsync();
        var one = await NewResellerAsync(admin);
        var two = await NewResellerAsync(admin);
        await SetBrandAsync(two.Client, name: "Marca de dos");
        var support = await ApiFixture.CreateUserAsync(admin, Roles.PlatformSupport, null);
        using var supportClient = api.ClientFor(await api.LoginOkAsync(support.Email, support.Password));
        var tenantId = await api.CreateTenantAsync("Sin marca SAC");
        var ownerUser = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        using var owner = api.ClientFor(await api.LoginOkAsync(ownerUser.Email, ownerUser.Password));

        // The reseller edits its own brand through its own route, which takes the reseller from the token; the platform routes are not its own.
        Assert.Equal(HttpStatusCode.Forbidden, (await SetBrandAsync(one.Client, "Intruso", path: $"/api/v1/platform/resellers/{two.Row.Id}/branding")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await one.Client.GetAsync($"/api/v1/platform/resellers/{two.Row.Id}/branding")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetLogoAsync(one.Client, Png(), $"/api/v1/platform/resellers/{two.Row.Id}/branding/logo")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await one.Client.DeleteAsync($"/api/v1/platform/resellers/{two.Row.Id}/branding/logo")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await SetBrandAsync(owner)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/v1/reseller/branding")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetBrandAsync(supportClient)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await supportClient.GetAsync($"/api/v1/platform/resellers/{two.Row.Id}/branding")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetBrandAsync(supportClient, "Soporte", path: $"/api/v1/platform/resellers/{two.Row.Id}/branding")).StatusCode);
        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/v1/reseller/branding", new { brandName = "Anónimo", primaryColor = "#0b5394" })).StatusCode);

        // The platform can edit any of them.
        Assert.Equal(HttpStatusCode.OK, (await SetBrandAsync(admin, "Corregida por la plataforma", path: $"/api/v1/platform/resellers/{two.Row.Id}/branding")).StatusCode);
        Assert.Equal("Corregida por la plataforma", (await two.Client.GetFromJsonAsync<Settings>("/api/v1/reseller/branding", ApiFixture.JsonOptions))!.BrandName);
    }

    [Fact]
    public async Task The_users_of_a_reseller_and_of_its_tenants_see_its_brand_and_nobody_else_does()
    {
        using var admin = await api.AdminClientAsync();
        var reseller = await NewResellerAsync(admin);
        await SetBrandAsync(reseller.Client);
        var opened = await reseller.Client.PostAsJsonAsync("/api/v1/reseller/tenants", new { name = "Cliente de marca SAC", environment = "Sandbox" });
        var tenantId = JsonDocument.Parse(await opened.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var ownerEmail = $"{Guid.NewGuid():N}@cliente.test";
        await reseller.Client.PostAsJsonAsync($"/api/v1/reseller/tenants/{tenantId}/owner", new { email = ownerEmail, displayName = "Dueña", password = ApiFixture.StrongPassword });
        using var customer = api.ClientFor(await api.LoginOkAsync(ownerEmail, ApiFixture.StrongPassword));

        Assert.Equal("Distribuidora Andina", (await customer.GetFromJsonAsync<Brand>("/api/v1/branding/current", ApiFixture.JsonOptions))!.BrandName);
        Assert.Equal("Distribuidora Andina", (await reseller.Client.GetFromJsonAsync<Brand>("/api/v1/branding/current", ApiFixture.JsonOptions))!.BrandName);

        // A direct tenant, and the platform itself, keep the default look.
        var directTenant = await api.CreateTenantAsync("Directa SAC");
        var direct = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, directTenant);
        using var directClient = api.ClientFor(await api.LoginOkAsync(direct.Email, direct.Password));
        Assert.Equal(HttpStatusCode.NoContent, (await directClient.GetAsync("/api/v1/branding/current")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.GetAsync("/api/v1/branding/current")).StatusCode);
        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/branding/current")).StatusCode);

        // A reseller that is switched off stops showing its brand; its customers keep working with the default look.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{reseller.Row.Id}", new { name = reseller.Row.Name, isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await customer.GetAsync("/api/v1/branding/current")).StatusCode);
    }

    [Fact]
    public async Task What_a_visitor_can_read_has_nothing_secret_and_every_change_is_audited()
    {
        using var admin = await api.AdminClientAsync();
        var reseller = await NewResellerAsync(admin);
        var host = NewHost();
        await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{reseller.Row.Id}/host", new { host });
        await SetBrandAsync(reseller.Client);
        await SetLogoAsync(reseller.Client, Png());

        var publicJson = await (await ForHostAsync(host)).Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(publicJson);
        Assert.Equal(["brandName", "logoUrl", "primaryColor", "supportEmail"], document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());

        var events = (await admin.GetFromJsonAsync<List<JsonElement>>("/api/v1/audit?entityType=reseller&take=50", ApiFixture.JsonOptions))!
            .Where(e => e.GetProperty("entityId").GetString() == reseller.Row.Id.ToString("D")).ToList();
        Assert.True(events.Count(e => e.GetProperty("action").GetString() == "tenancy.reseller.branding_updated") >= 2); // the domain and the brand
        var logo = Assert.Single(events, e => e.GetProperty("action").GetString() == "tenancy.reseller.logo_changed");
        Assert.DoesNotContain("iVBOR", logo.GetProperty("newValues").GetRawText(), StringComparison.Ordinal); // the bytes of the logo are not in the audit trail
    }
}
