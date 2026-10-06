using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecureFact.Identity.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>Resellers (ADR-043): who they are, what each one sees and does, and that no reseller reaches the tenants or the plans of another.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ResellersApiTests(ApiFixture api)
{
    private sealed record ResellerRow(Guid Id, string Name, bool IsActive, int TenantCount);

    private sealed record TenantRow(Guid Id, string Name, string Status, Guid? ResellerId, Guid PlanId);

    private sealed record PlanRow(Guid Id, string Code, string Name, bool IsActive, Guid? ResellerId);

    private sealed record Usage(PlanRow Plan, string Period);

    private sealed record Reseller(ResellerRow Row, HttpClient Client, string Email, string Password);

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private static async Task<ResellerRow> NewResellerRowAsync(HttpClient admin, string? name = null)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/platform/resellers", new { name = name ?? $"Revendedor {Guid.NewGuid():N}"[..20] });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ResellerRow>(ApiFixture.JsonOptions))!;
    }

    private static Task<HttpResponseMessage> CreateResellerUserAsync(HttpClient admin, Guid? resellerId, string email, string role = Roles.ResellerAdmin) =>
        admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "Admin de revendedor", password = ApiFixture.StrongPassword, roles = new[] { role }, resellerId });

    private async Task<Reseller> NewResellerAsync(HttpClient admin, string? name = null)
    {
        var row = await NewResellerRowAsync(admin, name);
        var email = $"{Guid.NewGuid():N}@reseller.test";
        Assert.Equal(HttpStatusCode.Created, (await CreateResellerUserAsync(admin, row.Id, email)).StatusCode);
        return new Reseller(row, api.ClientFor(await api.LoginOkAsync(email, ApiFixture.StrongPassword)), email, ApiFixture.StrongPassword);
    }

    private static async Task<TenantRow> OpenTenantAsync(HttpClient reseller, string name, Guid? planId = null)
    {
        var response = await reseller.PostAsJsonAsync("/api/v1/reseller/tenants", new { name, environment = "Sandbox", planId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TenantRow>(ApiFixture.JsonOptions))!;
    }

    private static async Task<PlanRow> NewPlanAsync(HttpClient admin, Guid? resellerId = null, bool isActive = true)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/platform/plans", new { code = $"rp-{Guid.NewGuid():N}"[..14], name = "Plan de revendedor", maxCompanies = 3, resellerId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var plan = (await response.Content.ReadFromJsonAsync<PlanRow>(ApiFixture.JsonOptions))!;
        if (!isActive)
        {
            await admin.PutAsJsonAsync($"/api/v1/platform/plans/{plan.Id}", new { code = plan.Code, name = plan.Name, maxCompanies = 3, isActive = false, resellerId });
        }

        return plan;
    }

    [Fact]
    public async Task The_super_admin_manages_resellers_and_the_rest_cannot()
    {
        using var admin = await api.AdminClientAsync();
        var support = await ApiFixture.CreateUserAsync(admin, Roles.PlatformSupport, null);
        using var supportClient = api.ClientFor(await api.LoginOkAsync(support.Email, support.Password));
        var tenantId = await api.CreateTenantAsync("Cuenta de un dueño SAC");
        var ownerUser = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        using var owner = api.ClientFor(await api.LoginOkAsync(ownerUser.Email, ownerUser.Password));

        var created = await NewResellerRowAsync(admin, "Distribuidora Andina");
        var listed = (await supportClient.GetFromJsonAsync<List<ResellerRow>>("/api/v1/platform/resellers", ApiFixture.JsonOptions))!;
        Assert.Contains(listed, r => r.Id == created.Id && r.TenantCount == 0);

        Assert.Equal(HttpStatusCode.Forbidden, (await supportClient.PostAsJsonAsync("/api/v1/platform/resellers", new { name = "No debe crearse" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/v1/platform/resellers")).StatusCode);
        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/platform/resellers")).StatusCode);

        Assert.Equal("SF-RES-002", await CodeAsync(await admin.PostAsJsonAsync("/api/v1/platform/resellers", new { name = "ab" })));
        var renamed = await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{created.Id}", new { name = "Distribuidora Andina Perú", isActive = true });
        Assert.Equal("Distribuidora Andina Perú", (await renamed.Content.ReadFromJsonAsync<ResellerRow>(ApiFixture.JsonOptions))!.Name);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{Guid.NewGuid()}", new { name = "Fantasma", isActive = true })).StatusCode);
    }

    [Fact]
    public async Task A_reseller_user_needs_a_reseller_and_only_that_role_takes_one()
    {
        using var admin = await api.AdminClientAsync();
        var row = await NewResellerRowAsync(admin);

        Assert.Equal(HttpStatusCode.Forbidden, (await CreateResellerUserAsync(admin, null, $"{Guid.NewGuid():N}@reseller.test")).StatusCode);
        var unknown = await CreateResellerUserAsync(admin, Guid.NewGuid(), $"{Guid.NewGuid():N}@reseller.test");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("SF-RES-001", await CodeAsync(unknown));
        // The reseller id belongs to the ResellerAdmin role only.
        var tenantId = await api.CreateTenantAsync("Con revendedor suelto SAC");
        var misplaced = await admin.PostAsJsonAsync("/api/v1/users", new { email = $"{Guid.NewGuid():N}@securefact.test", displayName = "Dueño", password = ApiFixture.StrongPassword, roles = new[] { Roles.TenantOwner }, tenantId, resellerId = row.Id });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, misplaced.StatusCode);

        var email = $"{Guid.NewGuid():N}@reseller.test";
        Assert.Equal(HttpStatusCode.Created, (await CreateResellerUserAsync(admin, row.Id, email)).StatusCode);
        using var client = api.ClientFor(await api.LoginOkAsync(email, ApiFixture.StrongPassword));
        var own = (await client.GetFromJsonAsync<ResellerRow>("/api/v1/reseller", ApiFixture.JsonOptions))!;
        Assert.Equal(row.Id, own.Id);
    }

    [Fact]
    public async Task A_reseller_opens_tenants_with_their_owner_and_sees_only_its_own()
    {
        using var admin = await api.AdminClientAsync();
        var one = await NewResellerAsync(admin);
        var two = await NewResellerAsync(admin);
        var unassigned = await api.CreateTenantAsync("Sin revendedor SAC");

        var mine = await OpenTenantAsync(one.Client, "Cliente uno SAC");
        var theirs = await OpenTenantAsync(two.Client, "Cliente dos SAC");
        Assert.Equal(one.Row.Id, mine.ResellerId);

        var ownerEmail = $"{Guid.NewGuid():N}@cliente.test";
        var owner = await one.Client.PostAsJsonAsync($"/api/v1/reseller/tenants/{mine.Id}/owner", new { email = ownerEmail, displayName = "Dueña del cliente", password = ApiFixture.StrongPassword });
        Assert.Equal(HttpStatusCode.Created, owner.StatusCode);
        using var ownerClient = api.ClientFor(await api.LoginOkAsync(ownerEmail, ApiFixture.StrongPassword)); // the customer can sign in and works in its own tenant
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync("/api/v1/companies")).StatusCode);
        // The owner is created once: after that the customer administers its own users.
        var again = await one.Client.PostAsJsonAsync($"/api/v1/reseller/tenants/{mine.Id}/owner", new { email = $"{Guid.NewGuid():N}@cliente.test", displayName = "Otro", password = ApiFixture.StrongPassword });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var listOne = (await one.Client.GetFromJsonAsync<List<TenantRow>>("/api/v1/reseller/tenants", ApiFixture.JsonOptions))!;
        Assert.Equal(mine.Id, Assert.Single(listOne).Id);

        // What belongs to another reseller, or to nobody, answers as if it did not exist.
        foreach (var foreign in new[] { theirs.Id, unassigned, Guid.NewGuid() })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await one.Client.GetAsync($"/api/v1/reseller/tenants/{foreign}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await one.Client.GetAsync($"/api/v1/reseller/tenants/{foreign}/usage")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await one.Client.PostAsJsonAsync($"/api/v1/reseller/tenants/{foreign}/plan", new { planId = Guid.NewGuid() })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await one.Client.PostAsJsonAsync($"/api/v1/reseller/tenants/{foreign}/owner", new { email = $"{Guid.NewGuid():N}@cliente.test", displayName = "Intruso", password = ApiFixture.StrongPassword })).StatusCode);
        }

        Assert.Equal(1, (await admin.GetFromJsonAsync<List<ResellerRow>>("/api/v1/platform/resellers", ApiFixture.JsonOptions))!.Single(r => r.Id == one.Row.Id).TenantCount);
        var usage = (await one.Client.GetFromJsonAsync<Usage>($"/api/v1/reseller/tenants/{mine.Id}/usage", ApiFixture.JsonOptions))!;
        Assert.Equal("pilot", usage.Plan.Code);
    }

    [Fact]
    public async Task A_reseller_reaches_nothing_of_the_platform_nor_of_the_tenants()
    {
        using var admin = await api.AdminClientAsync();
        var reseller = await NewResellerAsync(admin);
        var tenant = await OpenTenantAsync(reseller.Client, "Cliente cerrado SAC");

        foreach (var path in new[]
        {
            "/api/v1/platform/tenants", $"/api/v1/platform/tenants/{tenant.Id}", "/api/v1/platform/plans", "/api/v1/platform/resellers", $"/api/v1/platform/tenants/{tenant.Id}/usage",
            "/api/v1/users", "/api/v1/audit", "/api/v1/companies", "/api/v1/documents", "/api/v1/tenants/current", "/api/v1/plan",
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await reseller.Client.GetAsync(path)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await reseller.Client.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/status", new { status = "Suspended", reason = "No le corresponde" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reseller.Client.PostAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/reseller", new { resellerId = (Guid?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reseller.Client.PostAsJsonAsync("/api/v1/platform/plans", new { code = "robado", name = "Plan propio", maxCompanies = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reseller.Client.PostAsJsonAsync("/api/v1/users", new { email = $"{Guid.NewGuid():N}@x.test", displayName = "Otro", password = ApiFixture.StrongPassword, roles = new[] { Roles.TenantOwner }, tenantId = tenant.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reseller.Client.PostAsync($"/api/v1/outbox/dead/{Guid.NewGuid()}/requeue", null)).StatusCode);
    }

    [Fact]
    public async Task Plans_a_reseller_may_assign_are_the_public_ones_and_its_own_never_those_of_another()
    {
        using var admin = await api.AdminClientAsync();
        var one = await NewResellerAsync(admin);
        var two = await NewResellerAsync(admin);
        var publicPlan = await NewPlanAsync(admin);
        var privateOne = await NewPlanAsync(admin, one.Row.Id);
        var privateTwo = await NewPlanAsync(admin, two.Row.Id);
        var retired = await NewPlanAsync(admin, one.Row.Id, isActive: false);

        var visible = (await one.Client.GetFromJsonAsync<List<PlanRow>>("/api/v1/reseller/plans", ApiFixture.JsonOptions))!.Select(p => p.Id).ToHashSet();
        Assert.Contains(publicPlan.Id, visible);
        Assert.Contains(privateOne.Id, visible);
        Assert.DoesNotContain(privateTwo.Id, visible);
        Assert.DoesNotContain(retired.Id, visible);

        var tenant = await OpenTenantAsync(one.Client, "Con plan propio SAC", privateOne.Id);
        Assert.Equal(privateOne.Id, tenant.PlanId);

        var foreignPlan = await one.Client.PostAsJsonAsync("/api/v1/reseller/tenants", new { name = "Con plan ajeno SAC", environment = "Sandbox", planId = privateTwo.Id });
        Assert.Equal(HttpStatusCode.NotFound, foreignPlan.StatusCode);
        Assert.Equal("SF-PLAN-002", await CodeAsync(foreignPlan));
        var retiredPlan = await one.Client.PostAsJsonAsync($"/api/v1/reseller/tenants/{tenant.Id}/plan", new { planId = retired.Id });
        Assert.Equal("SF-PLAN-003", await CodeAsync(retiredPlan));

        var moved = await one.Client.PostAsJsonAsync($"/api/v1/reseller/tenants/{tenant.Id}/plan", new { planId = publicPlan.Id });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(publicPlan.Id, (await moved.Content.ReadFromJsonAsync<TenantRow>(ApiFixture.JsonOptions))!.PlanId);
        // A plan of a reseller that does not exist cannot be created.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync("/api/v1/platform/plans", new { code = $"x-{Guid.NewGuid():N}"[..12], name = "Huérfano", resellerId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task Moving_a_tenant_between_resellers_takes_it_from_one_view_to_the_other_and_is_audited()
    {
        using var admin = await api.AdminClientAsync();
        var one = await NewResellerAsync(admin);
        var two = await NewResellerAsync(admin);
        var tenantId = await api.CreateTenantAsync("Cuenta que se mueve SAC");

        Assert.Equal(HttpStatusCode.NotFound, (await one.Client.GetAsync($"/api/v1/reseller/tenants/{tenantId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/reseller", new { resellerId = one.Row.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await one.Client.GetAsync($"/api/v1/reseller/tenants/{tenantId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/reseller", new { resellerId = two.Row.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await one.Client.GetAsync($"/api/v1/reseller/tenants/{tenantId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await two.Client.GetAsync($"/api/v1/reseller/tenants/{tenantId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/reseller", new { resellerId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/reseller", new { resellerId = (Guid?)null })).StatusCode);

        var events = (await admin.GetFromJsonAsync<List<JsonElement>>($"/api/v1/audit?tenantId={tenantId}&entityType=tenant&take=20", ApiFixture.JsonOptions))!;
        Assert.Equal(3, events.Count(e => e.GetProperty("action").GetString() == "tenancy.tenant.reseller_changed"));
    }

    [Fact]
    public async Task A_reseller_switched_off_is_refused_and_its_tenants_keep_working_and_it_comes_back_when_switched_on()
    {
        using var admin = await api.AdminClientAsync();
        var reseller = await NewResellerAsync(admin);
        var tenant = await OpenTenantAsync(reseller.Client, "Cliente que sigue SAC");
        var ownerEmail = $"{Guid.NewGuid():N}@cliente.test";
        await reseller.Client.PostAsJsonAsync($"/api/v1/reseller/tenants/{tenant.Id}/owner", new { email = ownerEmail, displayName = "Dueño", password = ApiFixture.StrongPassword });
        using var ownerClient = api.ClientFor(await api.LoginOkAsync(ownerEmail, ApiFixture.StrongPassword));

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{reseller.Row.Id}", new { name = reseller.Row.Name, isActive = false })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await reseller.Client.GetAsync("/api/v1/reseller/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.LoginAsync(reseller.Email, reseller.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync("/api/v1/companies")).StatusCode); // the customer is not affected

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{reseller.Row.Id}", new { name = reseller.Row.Name, isActive = true })).StatusCode);
        using var back = api.ClientFor(await api.LoginOkAsync(reseller.Email, reseller.Password));
        Assert.Equal(HttpStatusCode.OK, (await back.GetAsync("/api/v1/reseller/tenants")).StatusCode);
    }

    [Fact]
    public async Task What_a_reseller_does_is_audited_in_the_account_it_touches()
    {
        using var admin = await api.AdminClientAsync();
        var reseller = await NewResellerAsync(admin);
        var plan = await NewPlanAsync(admin, reseller.Row.Id);
        var tenant = await OpenTenantAsync(reseller.Client, "Cuenta auditada SAC");
        await reseller.Client.PostAsJsonAsync($"/api/v1/reseller/tenants/{tenant.Id}/plan", new { planId = plan.Id });

        var events = (await admin.GetFromJsonAsync<List<JsonElement>>($"/api/v1/audit?tenantId={tenant.Id}&entityType=tenant&take=20", ApiFixture.JsonOptions))!;

        var created = Assert.Single(events, e => e.GetProperty("action").GetString() == "tenancy.tenant.created");
        Assert.Contains(reseller.Row.Id.ToString(), created.GetProperty("newValues").GetRawText(), StringComparison.Ordinal);
        var changed = Assert.Single(events, e => e.GetProperty("action").GetString() == "tenancy.tenant.plan_changed");
        Assert.Contains("byReseller", changed.GetProperty("newValues").GetRawText(), StringComparison.Ordinal);
    }
}
