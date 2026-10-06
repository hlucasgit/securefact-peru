using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecureFact.Identity.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>Platform administration of tenants (ADR-041): the list, the status changes and what a suspended tenant can no longer do.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class PlatformTenantsApiTests(ApiFixture api)
{
    private sealed record TenantRow(Guid Id, string Name, string Status, string Environment);

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private static Task<HttpResponseMessage> SetStatusAsync(HttpClient client, Guid tenantId, string status, string reason = "Falta de pago de la suscripción") =>
        client.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/status", new { status, reason });

    private async Task<(Guid TenantId, TestUser Owner)> NewTenantWithOwnerAsync(string name)
    {
        var tenantId = await api.CreateTenantAsync(name);
        using var admin = await api.AdminClientAsync();
        return (tenantId, await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId));
    }

    [Fact]
    public async Task Platform_staff_list_and_search_tenants_and_filter_by_status()
    {
        var unique = $"Listable {Guid.NewGuid():N}"[..20];
        var first = await api.CreateTenantAsync($"{unique} Alfa");
        await api.CreateTenantAsync($"{unique} Beta");
        using var admin = await api.AdminClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(admin, first, "Suspended")).StatusCode);

        var all = (await admin.GetFromJsonAsync<List<TenantRow>>($"/api/v1/platform/tenants?search={Uri.EscapeDataString(unique)}", ApiFixture.JsonOptions))!;
        var suspended = (await admin.GetFromJsonAsync<List<TenantRow>>($"/api/v1/platform/tenants?search={Uri.EscapeDataString(unique)}&status=Suspended", ApiFixture.JsonOptions))!;

        Assert.Equal(2, all.Count);
        Assert.Equal(["Alfa", "Beta"], all.Select(t => t.Name.Split(' ')[^1]).ToArray());
        Assert.Equal(first, Assert.Single(suspended).Id);
        // The wildcards of the search are text, not patterns.
        Assert.Empty((await admin.GetFromJsonAsync<List<TenantRow>>("/api/v1/platform/tenants?search=%25", ApiFixture.JsonOptions))!);
    }

    [Fact]
    public async Task Only_platform_staff_see_the_list_and_only_the_super_admin_changes_the_status()
    {
        var (tenantId, owner) = await NewTenantWithOwnerAsync("Gobierno de acceso SAC");
        using var admin = await api.AdminClientAsync();
        var support = await ApiFixture.CreateUserAsync(admin, Roles.PlatformSupport, null);
        using var supportClient = api.ClientFor(await api.LoginOkAsync(support.Email, support.Password));
        using var ownerClient = api.ClientFor(await api.LoginOkAsync(owner.Email, owner.Password));

        // Support reads the list but cannot change a status; a tenant owner is not platform staff.
        Assert.Equal(HttpStatusCode.OK, (await supportClient.GetAsync("/api/v1/platform/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetStatusAsync(supportClient, tenantId, "Suspended")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.GetAsync("/api/v1/platform/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetStatusAsync(ownerClient, tenantId, "Suspended")).StatusCode);
        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/platform/tenants")).StatusCode);

        Assert.Equal("Active", (await admin.GetFromJsonAsync<TenantRow>($"/api/v1/platform/tenants/{tenantId}", ApiFixture.JsonOptions))!.Status);
    }

    [Fact]
    public async Task A_suspended_tenant_cannot_sign_in_renew_or_use_the_api_and_is_back_when_reactivated()
    {
        var (tenantId, owner) = await NewTenantWithOwnerAsync("Suspendible SAC");
        var tokens = await api.LoginOkAsync(owner.Email, owner.Password);
        using var ownerClient = api.ClientFor(tokens);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync("/api/v1/companies")).StatusCode);
        using var admin = await api.AdminClientAsync();

        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(admin, tenantId, "Suspended")).StatusCode);

        // The access token is refused, the sign-in names the reason (after the right password), the wrong password still looks like any other, and the refresh token is not renewed.
        Assert.Equal(HttpStatusCode.Unauthorized, (await ownerClient.GetAsync("/api/v1/companies")).StatusCode);
        var login = await api.LoginAsync(owner.Email, owner.Password);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("SF-TEN-002", await CodeAsync(login));
        Assert.Equal("SF-AUTH-004", await CodeAsync(await api.LoginAsync(owner.Email, "Incorrecta-123456!")));
        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken })).StatusCode);

        // Platform staff keep working on the suspended tenant, and reactivating gives everything back.
        Assert.Equal("Suspended", (await admin.GetFromJsonAsync<TenantRow>($"/api/v1/platform/tenants/{tenantId}", ApiFixture.JsonOptions))!.Status);
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(admin, tenantId, "Active", "Pago regularizado")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync("/api/v1/companies")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.LoginAsync(owner.Email, owner.Password)).StatusCode);
    }

    [Fact]
    public async Task A_closed_tenant_is_final_and_status_changes_are_validated()
    {
        var (tenantId, owner) = await NewTenantWithOwnerAsync("Cerrable SAC");
        using var admin = await api.AdminClientAsync();

        // Reason: 3 to 300 characters. Same status and unknown tenant are refused.
        Assert.Equal("SF-TEN-003", await CodeAsync(await SetStatusAsync(admin, tenantId, "Suspended", "  ")));
        Assert.Equal("SF-TEN-003", await CodeAsync(await SetStatusAsync(admin, tenantId, "Suspended", new string('x', 301))));
        Assert.Equal("SF-TEN-003", await CodeAsync(await SetStatusAsync(admin, tenantId, "Active")));
        Assert.Equal(HttpStatusCode.NotFound, (await SetStatusAsync(admin, Guid.NewGuid(), "Suspended")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(admin, tenantId, "Closed", "Cierre a pedido del contribuyente")).StatusCode);

        foreach (var target in new[] { "Active", "Suspended", "Closed" })
        {
            var again = await SetStatusAsync(admin, tenantId, target);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
            Assert.Equal("SF-TEN-003", await CodeAsync(again));
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await api.LoginAsync(owner.Email, owner.Password)).StatusCode);
    }

    [Fact]
    public async Task Every_status_change_is_audited_with_the_reason_and_the_previous_status()
    {
        var tenantId = await api.CreateTenantAsync("Auditado SAC");
        using var admin = await api.AdminClientAsync();

        await SetStatusAsync(admin, tenantId, "Suspended", "Uso indebido del servicio");
        await SetStatusAsync(admin, tenantId, "Active", "Revisión concluida");

        var events = (await admin.GetFromJsonAsync<List<JsonElement>>($"/api/v1/audit?tenantId={tenantId}&entityType=tenant&take=20", ApiFixture.JsonOptions))!;
        var suspended = Assert.Single(events, e => e.GetProperty("action").GetString() == "tenancy.tenant.suspended");
        Assert.Contains("Uso indebido del servicio", suspended.GetProperty("newValues").GetRawText(), StringComparison.Ordinal);
        Assert.Contains("Active", suspended.GetProperty("oldValues").GetRawText(), StringComparison.Ordinal);
        Assert.Contains(events, e => e.GetProperty("action").GetString() == "tenancy.tenant.reactivated");
        Assert.True((await admin.PostAsync($"/api/v1/audit/verify?tenantId={tenantId}", null)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Platform_staff_list_the_users_of_one_tenant_and_a_tenant_never_sees_another()
    {
        var (firstTenant, firstOwner) = await NewTenantWithOwnerAsync("Usuarios Uno SAC");
        var (secondTenant, secondOwner) = await NewTenantWithOwnerAsync("Usuarios Dos SAC");
        using var admin = await api.AdminClientAsync();
        using var firstClient = api.ClientFor(await api.LoginOkAsync(firstOwner.Email, firstOwner.Password));

        var ofFirst = (await admin.GetFromJsonAsync<List<UserDto>>($"/api/v1/users?tenantId={firstTenant}", ApiFixture.JsonOptions))!;
        // A tenant owner asking for the users of another tenant gets none of them: the data scope decides, not the parameter.
        var spied = (await firstClient.GetFromJsonAsync<List<UserDto>>($"/api/v1/users?tenantId={secondTenant}", ApiFixture.JsonOptions))!;

        Assert.Equal(firstOwner.Email, Assert.Single(ofFirst).Email);
        Assert.Empty(spied);
        Assert.DoesNotContain(spied, user => user.Email == secondOwner.Email);
    }
}
