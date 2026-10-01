using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecureFact.Audit.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;
using SecureFact.SharedKernel.Domain;

namespace SecureFact.Security.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class OrganizationsApiTests(ApiFixture api)
{
    private static int _rucCounter = 10_000_000;

    private static string NewRuc()
    {
        var body = "20" + Interlocked.Increment(ref _rucCounter).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var sum = body.Select((c, i) => (c - '0') * weights[i]).Sum();
        var ruc = body + ((11 - (sum % 11)) % 10);
        Assert.True(Ruc.Create(ruc).IsSuccess);
        return ruc;
    }

    private static object Details(string legalName = "Distribuidora Andina SAC") => new
    {
        legalName,
        tradeName = "Andina",
        fiscalAddress = "Av. Larco 123, Miraflores",
        ubigeo = "150122",
        taxRegime = "RER",
        contactEmail = "facturacion@andina.pe",
    };

    private async Task<(Guid TenantId, HttpClient Owner)> NewTenantOwnerAsync(string name)
    {
        var tenantId = await api.CreateTenantAsync(name);
        using var admin = await api.AdminClientAsync();
        var owner = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        return (tenantId, api.ClientFor(await api.LoginOkAsync(owner.Email, owner.Password)));
    }

    private static async Task<CompanyDto> CreateCompanyAsync(HttpClient client, string? ruc = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/companies", new { ruc = ruc ?? NewRuc(), details = Details() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!;
    }

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task A_company_can_be_created_read_updated_and_listed()
    {
        var (tenantId, owner) = await NewTenantOwnerAsync("Org Basic SAC");
        var created = await CreateCompanyAsync(owner);

        Assert.Equal(tenantId, created.TenantId);
        Assert.Equal("America/Lima", created.TimeZone);
        Assert.Equal("PEN", created.DefaultCurrency);
        Assert.Equal(CompanyStatus.Active, created.Status);

        var update = await owner.PutAsJsonAsync($"/api/v1/companies/{created.Id}", Details("Nueva Razón Social SAC"));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!;
        Assert.Equal("Nueva Razón Social SAC", updated.LegalName);
        Assert.Equal(created.Ruc, updated.Ruc);

        var list = (await owner.GetFromJsonAsync<List<CompanyDto>>("/api/v1/companies", ApiFixture.JsonOptions))!;
        Assert.Contains(list, c => c.Id == created.Id);

        var events = (await owner.GetFromJsonAsync<List<AuditRecord>>("/api/v1/audit?take=200", ApiFixture.JsonOptions))!;
        Assert.Contains(events, e => e.Action == AuditActions.CompanyCreated && e.EntityId == created.Id.ToString("D"));
        var change = Assert.Single(events, e => e.Action == AuditActions.CompanyUpdated);
        Assert.Contains("Distribuidora Andina SAC", change.OldValues, StringComparison.Ordinal);
        Assert.Contains("Nueva Razón Social SAC", change.NewValues, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_input_is_rejected_with_stable_codes()
    {
        var (_, owner) = await NewTenantOwnerAsync("Org Validation SAC");

        var badRuc = await owner.PostAsJsonAsync("/api/v1/companies", new { ruc = "20100066604", details = Details() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badRuc.StatusCode);
        Assert.Equal("SF-VAL-001", await ProblemCodeAsync(badRuc));

        var badUbigeo = await owner.PostAsJsonAsync("/api/v1/companies", new
        {
            ruc = NewRuc(),
            details = new { legalName = "X SAC", fiscalAddress = "Calle 1", ubigeo = "15", },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badUbigeo.StatusCode);
        Assert.Equal("SF-ORG-001", await ProblemCodeAsync(badUbigeo));

        var badZone = await owner.PostAsJsonAsync("/api/v1/companies", new
        {
            ruc = NewRuc(),
            details = new { legalName = "X SAC", fiscalAddress = "Calle 1", ubigeo = "150101", timeZone = "Mars/Olympus" },
        });
        Assert.Equal("SF-ORG-001", await ProblemCodeAsync(badZone));
    }

    [Fact]
    public async Task The_same_ruc_cannot_be_registered_twice_within_a_tenant_but_can_in_another()
    {
        var (_, ownerA) = await NewTenantOwnerAsync("Org Dup A SAC");
        var (_, ownerB) = await NewTenantOwnerAsync("Org Dup B SAC");
        var ruc = NewRuc();
        await CreateCompanyAsync(ownerA, ruc);

        var duplicate = await ownerA.PostAsJsonAsync("/api/v1/companies", new { ruc, details = Details() });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("SF-ORG-003", await ProblemCodeAsync(duplicate));

        // Uniqueness is per tenant: another tenant is never told that a RUC exists elsewhere.
        var other = await CreateCompanyAsync(ownerB, ruc);
        Assert.Equal(ruc, other.Ruc);
    }

    [Fact]
    public async Task A_tenant_cannot_see_or_change_another_tenants_companies()
    {
        var (_, ownerA) = await NewTenantOwnerAsync("Org Iso A SAC");
        var (_, ownerB) = await NewTenantOwnerAsync("Org Iso B SAC");
        var companyOfA = await CreateCompanyAsync(ownerA);
        var establishment = await ownerA.PostAsJsonAsync($"/api/v1/companies/{companyOfA.Id}/establishments",
            new { code = "0000", details = new { name = "Sede", address = "Calle 1", ubigeo = "150101" } });
        Assert.Equal(HttpStatusCode.Created, establishment.StatusCode);

        var list = (await ownerB.GetFromJsonAsync<List<CompanyDto>>("/api/v1/companies", ApiFixture.JsonOptions))!;
        Assert.DoesNotContain(list, c => c.Id == companyOfA.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.GetAsync($"/api/v1/companies/{companyOfA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.PutAsJsonAsync($"/api/v1/companies/{companyOfA.Id}", Details("Hijack SAC"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.PostAsync($"/api/v1/companies/{companyOfA.Id}/deactivate", null)).StatusCode);
        Assert.Empty((await ownerB.GetFromJsonAsync<List<EstablishmentDto>>($"/api/v1/companies/{companyOfA.Id}/establishments", ApiFixture.JsonOptions))!);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.PostAsJsonAsync($"/api/v1/companies/{companyOfA.Id}/establishments",
            new { code = "0001", details = new { name = "Intruso", address = "Calle 2", ubigeo = "150101" } })).StatusCode);

        // A's company is untouched.
        var still = (await ownerA.GetFromJsonAsync<CompanyDto>($"/api/v1/companies/{companyOfA.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal(companyOfA.LegalName, still.LegalName);
    }

    [Fact]
    public async Task Platform_staff_cannot_read_or_create_tenant_business_data()
    {
        var (_, owner) = await NewTenantOwnerAsync("Org Platform SAC");
        var company = await CreateCompanyAsync(owner);
        using var admin = await api.AdminClientAsync();

        var list = (await admin.GetFromJsonAsync<List<CompanyDto>>("/api/v1/companies", ApiFixture.JsonOptions))!;
        Assert.DoesNotContain(list, c => c.Id == company.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/companies/{company.Id}")).StatusCode);

        var create = await admin.PostAsJsonAsync("/api/v1/companies", new { ruc = NewRuc(), details = Details() });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task Read_only_roles_can_read_but_not_manage_companies()
    {
        var (tenantId, owner) = await NewTenantOwnerAsync("Org Rbac SAC");
        var company = await CreateCompanyAsync(owner);
        var reader = await ApiFixture.CreateUserAsync(owner, Roles.ReadOnly, tenantId);
        using var readerClient = api.ClientFor(await api.LoginOkAsync(reader.Email, reader.Password));

        Assert.Equal(HttpStatusCode.OK, (await readerClient.GetAsync($"/api/v1/companies/{company.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync("/api/v1/companies", new { ruc = NewRuc(), details = Details() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PutAsJsonAsync($"/api/v1/companies/{company.Id}", Details("Nope SAC"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsync($"/api/v1/companies/{company.Id}/deactivate", null)).StatusCode);
    }

    [Fact]
    public async Task Establishments_enforce_unique_codes_and_belong_to_their_company()
    {
        var (_, owner) = await NewTenantOwnerAsync("Org Estab SAC");
        var company = await CreateCompanyAsync(owner);
        var other = await CreateCompanyAsync(owner);
        var body = new { code = "0001", details = new { name = "Tienda Centro", address = "Jr. Unión 1", ubigeo = "150101" } };

        var created = await owner.PostAsJsonAsync($"/api/v1/companies/{company.Id}/establishments", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var establishment = (await created.Content.ReadFromJsonAsync<EstablishmentDto>(ApiFixture.JsonOptions))!;

        var duplicate = await owner.PostAsJsonAsync($"/api/v1/companies/{company.Id}/establishments", body);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("SF-ORG-006", await ProblemCodeAsync(duplicate));

        var sameCodeOtherCompany = await owner.PostAsJsonAsync($"/api/v1/companies/{other.Id}/establishments", body);
        Assert.Equal(HttpStatusCode.Created, sameCodeOtherCompany.StatusCode);

        var badCode = await owner.PostAsJsonAsync($"/api/v1/companies/{company.Id}/establishments",
            new { code = "TOOLONG", details = new { name = "X", address = "Y", ubigeo = "150101" } });
        Assert.Equal("SF-ORG-004", await ProblemCodeAsync(badCode));

        var wrongParent = await owner.PutAsJsonAsync($"/api/v1/companies/{other.Id}/establishments/{establishment.Id}",
            new { name = "Renombrada", address = "Otra", ubigeo = "150101" });
        Assert.Equal(HttpStatusCode.NotFound, wrongParent.StatusCode);

        var rename = await owner.PutAsJsonAsync($"/api/v1/companies/{company.Id}/establishments/{establishment.Id}",
            new { name = "Renombrada", address = "Otra", ubigeo = "150101" });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);

        var list = (await owner.GetFromJsonAsync<List<EstablishmentDto>>($"/api/v1/companies/{company.Id}/establishments", ApiFixture.JsonOptions))!;
        Assert.Equal("Renombrada", Assert.Single(list).Name);
    }

    [Fact]
    public async Task Deactivated_companies_are_kept_for_traceability_and_accept_no_new_establishments()
    {
        var (_, owner) = await NewTenantOwnerAsync("Org Deactivate SAC");
        var company = await CreateCompanyAsync(owner);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/companies/{company.Id}/deactivate", null)).StatusCode);

        var read = (await owner.GetFromJsonAsync<CompanyDto>($"/api/v1/companies/{company.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal(CompanyStatus.Inactive, read.Status);
        var add = await owner.PostAsJsonAsync($"/api/v1/companies/{company.Id}/establishments",
            new { code = "0000", details = new { name = "Sede", address = "Calle 1", ubigeo = "150101" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, add.StatusCode);
    }
}
