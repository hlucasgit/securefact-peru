using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecureFact.Billing.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>Plans (ADR-042): the catalogue, assigning one to a tenant, and the limits that the modules enforce.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class PlansApiTests(ApiFixture api)
{
    private static int _rucCounter = 15_000_000;

    private sealed record PlanRow(Guid Id, string Code, string Name, int? MaxCompanies, int? MaxUsers, int? MaxDocumentsPerMonth, bool IsActive);

    private sealed record UsageItem(int Used, int? Limit);

    private sealed record Usage(PlanRow Plan, string Period, UsageItem Companies, UsageItem Users, UsageItem DocumentsThisMonth);

    private static string NewRuc()
    {
        var body = "20" + Interlocked.Increment(ref _rucCounter).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var sum = body.Select((c, i) => (c - '0') * weights[i]).Sum();
        return body + ((11 - (sum % 11)) % 10);
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private static async Task<PlanRow> NewPlanAsync(HttpClient admin, int? companies = null, int? users = null, int? documents = null, string? code = null)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/platform/plans", new
        {
            code = code ?? $"plan-{Guid.NewGuid():N}"[..16],
            name = "Plan de prueba",
            maxCompanies = companies,
            maxUsers = users,
            maxDocumentsPerMonth = documents,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PlanRow>(ApiFixture.JsonOptions))!;
    }

    private static async Task AssignAsync(HttpClient admin, Guid tenantId, PlanRow plan) =>
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/plan", new { planId = plan.Id })).StatusCode);

    private static Task<HttpResponseMessage> NewCompanyAsync(HttpClient owner) =>
        owner.PostAsJsonAsync("/api/v1/companies", new { ruc = NewRuc(), details = new { legalName = "Emisora SAC", fiscalAddress = "Av. Larco 123", ubigeo = "150122" } });

    private async Task<(Guid TenantId, HttpClient Owner)> NewTenantAsync(string name, HttpClient admin, PlanRow? plan = null)
    {
        var tenantId = await api.CreateTenantAsync(name);
        if (plan is not null)
        {
            await AssignAsync(admin, tenantId, plan);
        }

        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        return (tenantId, api.ClientFor(await api.LoginOkAsync(user.Email, user.Password)));
    }

    [Fact]
    public async Task Every_tenant_starts_on_the_pilot_plan_that_limits_nothing()
    {
        using var admin = await api.AdminClientAsync();
        var tenantId = await api.CreateTenantAsync("Piloto SAC");

        var plans = (await admin.GetFromJsonAsync<List<PlanRow>>("/api/v1/platform/plans", ApiFixture.JsonOptions))!;
        var usage = (await admin.GetFromJsonAsync<Usage>($"/api/v1/platform/tenants/{tenantId}/usage", ApiFixture.JsonOptions))!;

        var pilot = Assert.Single(plans, p => p.Code == "pilot");
        Assert.Equal((null, null, null), (pilot.MaxCompanies, pilot.MaxUsers, pilot.MaxDocumentsPerMonth));
        Assert.Equal(pilot.Id, usage.Plan.Id);
        Assert.Equal(new UsageItem(0, null), usage.Companies);
    }

    [Fact]
    public async Task The_super_admin_manages_plans_and_the_rest_cannot()
    {
        using var admin = await api.AdminClientAsync();
        var (_, owner) = await NewTenantAsync("Dueño de plan SAC", admin);
        var support = await ApiFixture.CreateUserAsync(admin, Roles.PlatformSupport, null);
        using var supportClient = api.ClientFor(await api.LoginOkAsync(support.Email, support.Password));
        var body = new { code = $"tmp-{Guid.NewGuid():N}"[..14], name = "No debe crearse", maxCompanies = 1 };

        Assert.Equal(HttpStatusCode.Forbidden, (await supportClient.PostAsJsonAsync("/api/v1/platform/plans", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/v1/platform/plans", body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await supportClient.GetAsync("/api/v1/platform/plans")).StatusCode);
        // A tenant never sees the catalogue (other tenants' plans may be private offers), only its own plan.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/v1/platform/plans")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/plan")).StatusCode);
        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/plan")).StatusCode);
    }

    [Fact]
    public async Task A_plan_is_validated_its_code_is_unique_and_it_can_be_edited_and_retired()
    {
        using var admin = await api.AdminClientAsync();
        var plan = await NewPlanAsync(admin, companies: 2, users: 5, documents: 100);

        Assert.Equal("SF-PLAN-004", await CodeAsync(await admin.PostAsJsonAsync("/api/v1/platform/plans", new { code = plan.Code, name = "Otro", maxCompanies = 1 })));
        Assert.Equal("SF-PLAN-003", await CodeAsync(await admin.PostAsJsonAsync("/api/v1/platform/plans", new { code = "Con Espacios", name = "Malo" })));
        Assert.Equal("SF-PLAN-003", await CodeAsync(await admin.PostAsJsonAsync("/api/v1/platform/plans", new { code = $"neg-{Guid.NewGuid():N}"[..12], name = "Negativo", maxUsers = -1 })));

        var edited = await admin.PutAsJsonAsync($"/api/v1/platform/plans/{plan.Id}", new { code = "ignored", name = "Plan editado", maxCompanies = 3, maxUsers = (int?)null, maxDocumentsPerMonth = 200, isActive = false });
        var row = (await edited.Content.ReadFromJsonAsync<PlanRow>(ApiFixture.JsonOptions))!;
        Assert.Equal((plan.Code, "Plan editado", 3, (int?)null, 200, false), (row.Code, row.Name, row.MaxCompanies, row.MaxUsers, row.MaxDocumentsPerMonth, row.IsActive)); // the code never changes

        var tenantId = await api.CreateTenantAsync("Plan retirado SAC");
        Assert.Equal("SF-PLAN-003", await CodeAsync(await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/plan", new { planId = plan.Id })));
        Assert.Equal("SF-PLAN-002", await CodeAsync(await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/plan", new { planId = Guid.NewGuid() })));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{Guid.NewGuid()}/plan", new { planId = row.Id })).StatusCode);
    }

    [Fact]
    public async Task A_company_beyond_the_plan_is_refused_and_a_higher_limit_lets_it_through()
    {
        using var admin = await api.AdminClientAsync();
        var plan = await NewPlanAsync(admin, companies: 1);
        var (_, owner) = await NewTenantAsync("Una empresa SAC", admin, plan);

        Assert.Equal(HttpStatusCode.Created, (await NewCompanyAsync(owner)).StatusCode);
        var refused = await NewCompanyAsync(owner);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("SF-PLAN-001", await CodeAsync(refused));

        await admin.PutAsJsonAsync($"/api/v1/platform/plans/{plan.Id}", new { code = plan.Code, name = plan.Name, maxCompanies = 2 });
        Assert.Equal(HttpStatusCode.Created, (await NewCompanyAsync(owner)).StatusCode);
        var usage = (await owner.GetFromJsonAsync<Usage>("/api/v1/plan", ApiFixture.JsonOptions))!;
        Assert.Equal(new UsageItem(2, 2), usage.Companies);
    }

    [Fact]
    public async Task Users_beyond_the_plan_are_refused_and_deactivating_one_frees_the_place()
    {
        using var admin = await api.AdminClientAsync();
        var plan = await NewPlanAsync(admin, users: 2);
        var (tenantId, owner) = await NewTenantAsync("Dos usuarios SAC", admin, plan); // the owner takes the first place

        var second = await ApiFixture.CreateUserAsync(owner, Roles.Sales, tenantId);
        var refused = await owner.PostAsJsonAsync("/api/v1/users", new { email = $"{Guid.NewGuid():N}@securefact.test", displayName = "Tercero", password = ApiFixture.StrongPassword, roles = new[] { Roles.Sales } });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("SF-PLAN-001", await CodeAsync(refused));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/users/{second.Id}/deactivate", null)).StatusCode);
        await ApiFixture.CreateUserAsync(owner, Roles.Sales, tenantId);
        Assert.Equal(new UsageItem(2, 2), (await owner.GetFromJsonAsync<Usage>("/api/v1/plan", ApiFixture.JsonOptions))!.Users);
    }

    [Fact]
    public async Task Documents_beyond_the_monthly_allowance_are_refused_but_a_replayed_request_still_answers()
    {
        using var admin = await api.AdminClientAsync();
        var plan = await NewPlanAsync(admin, documents: 2);
        var (_, owner) = await NewTenantAsync("Dos comprobantes SAC", admin, plan);
        var company = (await (await NewCompanyAsync(owner)).Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!;
        var series = (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = "01", code = "F001" })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        async Task<HttpResponseMessage> IssueAsync(string key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
            {
                Content = JsonContent.Create(new
                {
                    seriesId = series.Id,
                    issueDate = DateTimeOffset.UtcNow.AddHours(-5).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    currency = "PEN",
                    buyer = new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
                    lines = new[] { new { description = "Servicio", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10" } } },
                }),
            };
            request.Headers.Add("Idempotency-Key", key);
            return await owner.SendAsync(request);
        }

        var firstKey = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.Created, (await IssueAsync(firstKey)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await IssueAsync(Guid.NewGuid().ToString("N"))).StatusCode);

        var refused = await IssueAsync(Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("SF-PLAN-001", await CodeAsync(refused));
        // A refusal leaves no gap in the numbering and no trace; and a retry of an accepted request is not a new document.
        Assert.Equal(HttpStatusCode.Created, (await IssueAsync(firstKey)).StatusCode);
        var usage = (await owner.GetFromJsonAsync<Usage>("/api/v1/plan", ApiFixture.JsonOptions))!;
        Assert.Equal(new UsageItem(2, 2), usage.DocumentsThisMonth);

        await admin.PutAsJsonAsync($"/api/v1/platform/plans/{plan.Id}", new { code = plan.Code, name = plan.Name, maxDocumentsPerMonth = 3 });
        var third = await IssueAsync(Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Created, third.StatusCode);
        Assert.Equal(3, (await third.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!.Number); // the refused attempt took no number
    }

    [Fact]
    public async Task Concurrent_requests_cannot_take_more_places_than_the_allowance()
    {
        using var admin = await api.AdminClientAsync();
        var plan = await NewPlanAsync(admin, documents: 3);
        var (_, owner) = await NewTenantAsync("Concurrentes SAC", admin, plan);
        var company = (await (await NewCompanyAsync(owner)).Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!;
        var series = (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = "01", code = "F001" })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
            {
                Content = JsonContent.Create(new
                {
                    seriesId = series.Id,
                    issueDate = DateTimeOffset.UtcNow.AddHours(-5).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    currency = "PEN",
                    buyer = new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
                    lines = new[] { new { description = "Servicio", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10" } } },
                }),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            return (await owner.SendAsync(request)).StatusCode;
        }));

        Assert.Equal(3, results.Count(r => r == HttpStatusCode.Created));
        Assert.Equal(5, results.Count(r => r == HttpStatusCode.Forbidden));
    }

    [Fact]
    public async Task A_tenant_reads_only_its_own_plan_and_the_usage_of_another_is_for_platform_staff()
    {
        using var admin = await api.AdminClientAsync();
        var (_, ownerA) = await NewTenantAsync("Aislada A SAC", admin, await NewPlanAsync(admin, companies: 1));
        var (tenantB, _) = await NewTenantAsync("Aislada B SAC", admin);
        Assert.Equal(HttpStatusCode.Created, (await NewCompanyAsync(ownerA)).StatusCode);

        var own = (await ownerA.GetFromJsonAsync<Usage>("/api/v1/plan", ApiFixture.JsonOptions))!;
        Assert.Equal(1, own.Plan.MaxCompanies);
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerA.GetAsync($"/api/v1/platform/tenants/{tenantB}/usage")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/platform/tenants/{Guid.NewGuid()}/usage")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/plan")).StatusCode); // platform staff have no plan of their own
    }

    [Fact]
    public async Task Plan_changes_are_audited_with_the_previous_plan()
    {
        using var admin = await api.AdminClientAsync();
        var plan = await NewPlanAsync(admin, companies: 5);
        var tenantId = await api.CreateTenantAsync("Plan auditado SAC");
        await AssignAsync(admin, tenantId, plan);

        var events = (await admin.GetFromJsonAsync<List<JsonElement>>($"/api/v1/audit?tenantId={tenantId}&entityType=tenant&take=20", ApiFixture.JsonOptions))!;

        var change = Assert.Single(events, e => e.GetProperty("action").GetString() == "tenancy.tenant.plan_changed");
        Assert.Contains(plan.Code, change.GetProperty("newValues").GetRawText(), StringComparison.Ordinal);
        Assert.Contains("planId", change.GetProperty("oldValues").GetRawText(), StringComparison.Ordinal);
    }
}
