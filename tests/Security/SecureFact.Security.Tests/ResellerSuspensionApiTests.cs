using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecureFact.Identity.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>A reseller suspends and reactivates the accounts it opened (ADR-045), and never undoes what the platform did.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ResellerSuspensionApiTests(ApiFixture api)
{
    private sealed record Row(Guid Id, string Name);

    private sealed record TenantRow(Guid Id, string Name, string Status, string? SuspendedBy);

    private sealed record Setup(HttpClient Reseller, Guid ResellerId, Guid TenantId, string OwnerEmail);

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private static Task<HttpResponseMessage> SetStatusAsync(HttpClient client, string path, string status, string reason = "Factura de marzo sin pagar") =>
        client.PostAsJsonAsync(path, new { status, reason });

    private async Task<Setup> NewSetupAsync(HttpClient admin)
    {
        var created = await admin.PostAsJsonAsync("/api/v1/platform/resellers", new { name = $"Cobranza {Guid.NewGuid():N}"[..18] });
        var reseller = (await created.Content.ReadFromJsonAsync<Row>(ApiFixture.JsonOptions))!;
        var email = $"{Guid.NewGuid():N}@reseller.test";
        await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "Admin", password = ApiFixture.StrongPassword, roles = new[] { Roles.ResellerAdmin }, resellerId = reseller.Id });
        var client = api.ClientFor(await api.LoginOkAsync(email, ApiFixture.StrongPassword));
        var opened = await client.PostAsJsonAsync("/api/v1/reseller/tenants", new { name = "Cliente moroso SAC", environment = "Sandbox" });
        var tenant = (await opened.Content.ReadFromJsonAsync<TenantRow>(ApiFixture.JsonOptions))!;
        var ownerEmail = $"{Guid.NewGuid():N}@cliente.test";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/v1/reseller/tenants/{tenant.Id}/owner", new { email = ownerEmail, displayName = "Dueño", password = ApiFixture.StrongPassword })).StatusCode);
        return new Setup(client, reseller.Id, tenant.Id, ownerEmail);
    }

    private static string Own(Setup s) => $"/api/v1/reseller/tenants/{s.TenantId}/status";

    private static string Platform(Setup s) => $"/api/v1/platform/tenants/{s.TenantId}/status";

    [Fact]
    public async Task A_reseller_suspends_its_account_the_customer_is_locked_out_and_reactivating_brings_it_back()
    {
        using var admin = await api.AdminClientAsync();
        var s = await NewSetupAsync(admin);
        var tokens = await api.LoginOkAsync(s.OwnerEmail, ApiFixture.StrongPassword);
        using var owner = api.ClientFor(tokens);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/companies")).StatusCode);

        var suspended = await SetStatusAsync(s.Reseller, Own(s), "Suspended");
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        var row = (await suspended.Content.ReadFromJsonAsync<TenantRow>(ApiFixture.JsonOptions))!;
        Assert.Equal(("Suspended", "Reseller"), (row.Status, row.SuspendedBy));

        // The same effect as a suspension of the platform: the open session stops serving at once, and signing in says why.
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/v1/companies")).StatusCode);
        var login = await api.LoginAsync(s.OwnerEmail, ApiFixture.StrongPassword);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("SF-TEN-002", await CodeAsync(login));
        Assert.Equal("Suspended", (await s.Reseller.GetFromJsonAsync<TenantRow>($"/api/v1/reseller/tenants/{s.TenantId}", ApiFixture.JsonOptions))!.Status);

        var back = await SetStatusAsync(s.Reseller, Own(s), "Active", "Pagó la factura");
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Null((await back.Content.ReadFromJsonAsync<TenantRow>(ApiFixture.JsonOptions))!.SuspendedBy);
        using var again = api.ClientFor(await api.LoginOkAsync(s.OwnerEmail, ApiFixture.StrongPassword));
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/api/v1/companies")).StatusCode);
    }

    [Fact]
    public async Task A_reseller_never_closes_an_account_and_the_reason_and_the_transition_are_checked()
    {
        using var admin = await api.AdminClientAsync();
        var s = await NewSetupAsync(admin);

        var close = await SetStatusAsync(s.Reseller, Own(s), "Closed");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, close.StatusCode);
        Assert.Equal("SF-TEN-003", await CodeAsync(close));
        Assert.Equal("SF-TEN-003", await CodeAsync(await SetStatusAsync(s.Reseller, Own(s), "Suspended", "ab")));
        Assert.Equal("SF-TEN-003", await CodeAsync(await SetStatusAsync(s.Reseller, Own(s), "Suspended", new string('x', 301))));
        Assert.Equal("SF-TEN-003", await CodeAsync(await SetStatusAsync(s.Reseller, Own(s), "Active"))); // already active

        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(s.Reseller, Own(s), "Suspended")).StatusCode);
        Assert.Equal("SF-TEN-003", await CodeAsync(await SetStatusAsync(s.Reseller, Own(s), "Suspended"))); // already suspended
        Assert.Equal("Suspended", (await s.Reseller.GetFromJsonAsync<TenantRow>($"/api/v1/reseller/tenants/{s.TenantId}", ApiFixture.JsonOptions))!.Status);
    }

    [Fact]
    public async Task A_reseller_cannot_lift_a_suspension_of_the_platform_and_the_platform_can_take_over_one_of_the_reseller()
    {
        using var admin = await api.AdminClientAsync();
        var s = await NewSetupAsync(admin);

        // The platform suspends for its own reasons: the reseller can neither lift it nor redo it.
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(admin, Platform(s), "Suspended", "Uso indebido del servicio")).StatusCode);
        var lift = await SetStatusAsync(s.Reseller, Own(s), "Active", "Quiero reactivarla");
        Assert.Equal(HttpStatusCode.Forbidden, lift.StatusCode);
        Assert.Equal("SF-TEN-004", await CodeAsync(lift));
        Assert.Equal("SF-TEN-003", await CodeAsync(await SetStatusAsync(s.Reseller, Own(s), "Suspended")));
        Assert.Equal("Platform", (await s.Reseller.GetFromJsonAsync<TenantRow>($"/api/v1/reseller/tenants/{s.TenantId}", ApiFixture.JsonOptions))!.SuspendedBy);
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(admin, Platform(s), "Active", "Revisado")).StatusCode);

        // The reseller suspends; then the platform suspends over it, and from then on the reseller cannot lift it.
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(s.Reseller, Own(s), "Suspended")).StatusCode);
        var takeover = await SetStatusAsync(admin, Platform(s), "Suspended", "Cuenta en investigación");
        Assert.Equal(HttpStatusCode.OK, takeover.StatusCode);
        Assert.Equal("Platform", (await takeover.Content.ReadFromJsonAsync<TenantRow>(ApiFixture.JsonOptions))!.SuspendedBy);
        Assert.Equal("SF-TEN-004", await CodeAsync(await SetStatusAsync(s.Reseller, Own(s), "Active")));
        // Suspending twice in a row stays invalid for the platform once the suspension is already its own.
        Assert.Equal("SF-TEN-003", await CodeAsync(await SetStatusAsync(admin, Platform(s), "Suspended", "Otra vez")));

        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(admin, Platform(s), "Active", "Concluida")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(s.Reseller, Own(s), "Suspended")).StatusCode); // free again
    }

    [Fact]
    public async Task A_reseller_touches_only_its_own_accounts_and_only_its_role_can()
    {
        using var admin = await api.AdminClientAsync();
        var one = await NewSetupAsync(admin);
        var two = await NewSetupAsync(admin);
        var unassigned = await api.CreateTenantAsync("Sin revendedor SAC");
        var ownerUser = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, unassigned);
        using var owner = api.ClientFor(await api.LoginOkAsync(ownerUser.Email, ownerUser.Password));
        var support = await ApiFixture.CreateUserAsync(admin, Roles.PlatformSupport, null);
        using var supportClient = api.ClientFor(await api.LoginOkAsync(support.Email, support.Password));

        foreach (var foreign in new[] { two.TenantId, unassigned, Guid.NewGuid() })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await SetStatusAsync(one.Reseller, $"/api/v1/reseller/tenants/{foreign}/status", "Suspended")).StatusCode);
        }

        // The platform route is not the reseller's, and the reseller route is not for a tenant user or for support.
        Assert.Equal(HttpStatusCode.Forbidden, (await SetStatusAsync(one.Reseller, Platform(one), "Suspended")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetStatusAsync(owner, Own(one), "Suspended")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetStatusAsync(supportClient, Own(one), "Suspended")).StatusCode);
        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await SetStatusAsync(anonymous, Own(one), "Suspended")).StatusCode);

        Assert.Equal("Active", (await admin.GetFromJsonAsync<TenantRow>($"/api/v1/platform/tenants/{two.TenantId}", ApiFixture.JsonOptions))!.Status);
        Assert.Equal("Active", (await admin.GetFromJsonAsync<TenantRow>($"/api/v1/platform/tenants/{unassigned}", ApiFixture.JsonOptions))!.Status);
    }

    [Fact]
    public async Task What_a_reseller_suspends_is_audited_with_its_reason_and_the_platform_sees_who_did_it()
    {
        using var admin = await api.AdminClientAsync();
        var s = await NewSetupAsync(admin);
        await SetStatusAsync(s.Reseller, Own(s), "Suspended", "Dos facturas vencidas");
        await SetStatusAsync(s.Reseller, Own(s), "Active", "Regularizó la deuda");

        var events = (await admin.GetFromJsonAsync<List<JsonElement>>($"/api/v1/audit?tenantId={s.TenantId}&entityType=tenant&take=30", ApiFixture.JsonOptions))!;

        var suspended = Assert.Single(events, e => e.GetProperty("action").GetString() == "tenancy.tenant.suspended");
        var values = suspended.GetProperty("newValues").GetRawText();
        Assert.Contains("Dos facturas vencidas", values, StringComparison.Ordinal);
        Assert.Contains(s.ResellerId.ToString(), values, StringComparison.Ordinal);
        Assert.Contains("Reseller", values, StringComparison.Ordinal);
        Assert.Contains(events, e => e.GetProperty("action").GetString() == "tenancy.tenant.reactivated");
        Assert.True((await admin.PostAsync($"/api/v1/audit/verify?tenantId={s.TenantId}", null)).IsSuccessStatusCode);

        // The platform sees who suspended: the reseller and its reason, the same as for its own suspensions.
        await SetStatusAsync(s.Reseller, Own(s), "Suspended");
        Assert.Equal("Reseller", (await admin.GetFromJsonAsync<TenantRow>($"/api/v1/platform/tenants/{s.TenantId}", ApiFixture.JsonOptions))!.SuspendedBy);
    }
}
