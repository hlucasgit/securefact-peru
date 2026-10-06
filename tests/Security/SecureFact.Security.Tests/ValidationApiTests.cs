using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SecureFact.Billing.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>
/// The input rules of the administration endpoints (companies, establishments, customers, users, series, notes): a request that breaks one is refused with a stable
/// error code before anything is stored, and the refusal leaves the data as it was.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ValidationApiTests(ApiFixture api)
{
    private static int _rucCounter = 29_000_000;

    private sealed record Setup(Guid TenantId, HttpClient Owner, CompanyDto Company, SeriesDto Invoice, SeriesDto Receipt, SeriesDto Credit);

    private static string NewRuc()
    {
        var body = "20" + Interlocked.Increment(ref _rucCounter).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var sum = body.Select((c, i) => (c - '0') * weights[i]).Sum();
        return body + ((11 - (sum % 11)) % 10);
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static DateOnly TodayInLima() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Lima")).DateTime);

    private static string PfxFor(string ruc)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=Representante Demo, OU={ruc}, O=EMISORA SAC, C=PE", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, "pw"));
    }

    private async Task<Setup> NewTenantAsync(string name)
    {
        api.Sunat.Reset();
        var tenantId = await api.CreateTenantAsync(name);
        using var admin = await api.AdminClientAsync();
        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        var owner = api.ClientFor(await api.LoginOkAsync(user.Email, user.Password));
        var ruc = NewRuc();
        var company = (await (await owner.PostAsJsonAsync("/api/v1/companies", new { ruc, details = Company() })).Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/certificates", new { companyId = company.Id, pfxBase64 = PfxFor(ruc), password = "pw" })).StatusCode);

        async Task<SeriesDto> SeriesAsync(string type, string code) =>
            (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = type, code })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        return new Setup(tenantId, owner, company, await SeriesAsync("01", "F001"), await SeriesAsync("03", "B001"), await SeriesAsync("07", "FC01"));
    }

    private static object Company(string legalName = "Emisora SAC", string? tradeName = null, string fiscalAddress = "Av. Larco 123", string ubigeo = "150122", string? regime = null,
        string? email = null, string timeZone = "America/Lima", string currency = "PEN") =>
        new { legalName, tradeName, fiscalAddress, ubigeo, taxRegime = regime, contactEmail = email, timeZone, defaultCurrency = currency };

    private static async Task<string> BodyAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    private static async Task ExpectRefusedAsync(string name, Task<HttpResponseMessage> pending, HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        var response = await pending;
        var body = await BodyAsync(response);
        Assert.True(response.StatusCode == status, $"{name}: {(int)response.StatusCode} {body}");
        using var problem = JsonDocument.Parse(body);
        Assert.StartsWith("SF-", problem.RootElement.GetProperty("code").GetString(), StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    // ---------- companies and establishments ----------

    [Fact]
    public async Task A_company_that_breaks_the_rules_is_refused_and_keeps_its_data()
    {
        var setup = await NewTenantAsync("Company Rules SAC");
        var url = $"/api/v1/companies/{setup.Company.Id}";

        var cases = new (string Name, object Details)[]
        {
            ("no legal name", Company(legalName: " ")),
            ("a long legal name", Company(legalName: new string('A', 251))),
            ("a long trade name", Company(tradeName: new string('A', 251))),
            ("no fiscal address", Company(fiscalAddress: "")),
            ("a long fiscal address", Company(fiscalAddress: new string('A', 251))),
            ("a short ubigeo", Company(ubigeo: "1501")),
            ("a ubigeo with letters", Company(ubigeo: "15012A")),
            ("a contact email that is not one", Company(email: "not-an-email")),
            ("a long tax regime", Company(regime: new string('R', 61))),
            ("an unknown time zone", Company(timeZone: "Nowhere/Land")),
            ("a currency of two letters", Company(currency: "PE")),
        };
        foreach (var (name, details) in cases)
        {
            await ExpectRefusedAsync(name, setup.Owner.PutAsJsonAsync(url, details));
        }

        await ExpectRefusedAsync("a RUC that does not check", setup.Owner.PostAsJsonAsync("/api/v1/companies", new { ruc = "20123456789", details = Company() }));
        await ExpectRefusedAsync("a short RUC", setup.Owner.PostAsJsonAsync("/api/v1/companies", new { ruc = "123", details = Company() }));

        var read = (await setup.Owner.GetFromJsonAsync<CompanyDto>(url, ApiFixture.JsonOptions))!;
        Assert.Equal("Emisora SAC", read.LegalName);
    }

    [Fact]
    public async Task An_establishment_is_validated_updated_and_deactivated_and_a_missing_one_is_not_found()
    {
        var setup = await NewTenantAsync("Establishment Rules SAC");
        var url = $"/api/v1/companies/{setup.Company.Id}/establishments";

        await ExpectRefusedAsync("no name", PostAsync(setup.Owner, url, new { code = "0001", details = new { name = "", address = "Av. 1", ubigeo = "150101" } }));
        await ExpectRefusedAsync("no address", PostAsync(setup.Owner, url, new { code = "0001", details = new { name = "Sucursal", address = "", ubigeo = "150101" } }));
        await ExpectRefusedAsync("a bad ubigeo", PostAsync(setup.Owner, url, new { code = "0001", details = new { name = "Sucursal", address = "Av. 1", ubigeo = "x" } }));
        await ExpectRefusedAsync("a bad code", PostAsync(setup.Owner, url, new { code = "AB", details = new { name = "Sucursal", address = "Av. 1", ubigeo = "150101" } }));

        var created = await setup.Owner.PostAsJsonAsync(url, new { code = "0001", details = new { name = "Sucursal", address = "Av. 1", ubigeo = "150101" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var establishment = (await created.Content.ReadFromJsonAsync<EstablishmentDto>(ApiFixture.JsonOptions))!;

        await ExpectRefusedAsync("an update with no name", setup.Owner.PutAsJsonAsync($"{url}/{establishment.Id}", new { name = " ", address = "Av. 2", ubigeo = "150101" }));
        var updated = await setup.Owner.PutAsJsonAsync($"{url}/{establishment.Id}", new { name = "Sucursal Norte", address = "Av. 2", ubigeo = "150102" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var deactivated = await setup.Owner.PostAsync($"{url}/{establishment.Id}/deactivate", null);
        Assert.True(deactivated.IsSuccessStatusCode, await BodyAsync(deactivated));
        Assert.False((await setup.Owner.GetFromJsonAsync<List<EstablishmentDto>>(url, ApiFixture.JsonOptions))!.Single(e => e.Id == establishment.Id).IsActive);

        await ExpectRefusedAsync("a missing establishment", setup.Owner.PostAsync($"{url}/{Guid.NewGuid()}/deactivate", null), HttpStatusCode.NotFound);

        // A series cannot use an inactive establishment or one of another company.
        await ExpectRefusedAsync("an inactive establishment", setup.Owner.PostAsJsonAsync("/api/v1/series", new { companyId = setup.Company.Id, documentTypeCode = "01", code = "F002", establishmentId = establishment.Id }));
        await ExpectRefusedAsync("an unknown establishment", setup.Owner.PostAsJsonAsync("/api/v1/series", new { companyId = setup.Company.Id, documentTypeCode = "01", code = "F002", establishmentId = Guid.NewGuid() }));
    }

    // ---------- customers ----------

    [Fact]
    public async Task A_customer_that_breaks_the_rules_is_refused()
    {
        var setup = await NewTenantAsync("Customer Rules SAC");

        object Customer(string type = "1", string number = "12345678", string name = "Cliente", string? address = null, string? email = null, string? phone = null) =>
            new { documentTypeCode = type, documentNumber = number, name, address, email, phone };

        var cases = new (string Name, object Body)[]
        {
            ("no name", Customer(name: " ")),
            ("a long name", Customer(name: new string('N', 251))),
            ("an unknown document type", Customer(type: "9")),
            ("a short DNI", Customer(number: "1234")),
            ("a long address", Customer(address: new string('A', 251))),
            ("a long phone", Customer(phone: new string('9', 31))),
            ("a bad email", Customer(email: "nope")),
        };
        foreach (var (name, body) in cases)
        {
            await ExpectRefusedAsync(name, setup.Owner.PostAsJsonAsync("/api/v1/customers", body));
        }

        var created = await setup.Owner.PostAsJsonAsync("/api/v1/customers", Customer());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var customer = (await created.Content.ReadFromJsonAsync<CustomerDtoShape>(ApiFixture.JsonOptions))!;
        await ExpectRefusedAsync("an update with a bad email", setup.Owner.PutAsJsonAsync($"/api/v1/customers/{customer.Id}", Customer(email: "nope")));
    }

    private sealed record CustomerDtoShape(Guid Id);

    // ---------- products ----------

    [Fact]
    public async Task A_product_that_breaks_the_rules_is_refused_and_its_code_cannot_change()
    {
        var setup = await NewTenantAsync("Product Rules SAC");

        object Product(string code = "SKU-1", string description = "Producto", int kind = 0, string unit = "NIU", decimal value = 10m, string affectation = "10", string? sunat = null, string? category = null) =>
            new { internalCode = code, description, kind, unitCode = unit, unitValue = value, igvAffectationCode = affectation, sunatProductCode = sunat, category };

        var cases = new (string Name, object Body)[]
        {
            ("no code", Product(code: "")),
            ("a code with spaces", Product(code: "bad code!")),
            ("a long code", Product(code: new string('C', 51))),
            ("no description", Product(description: " ")),
            ("a long description", Product(description: new string('D', 501))),
            ("an unknown kind", Product(kind: 7)),
            ("a long unit", Product(unit: "TOOLONG")),
            ("a negative value", Product(value: -1m)),
            ("a value with 11 decimals", Product(value: 1.12345678901m)),
            ("an unknown affectation", Product(affectation: "99")),
            ("a SUNAT code of three digits", Product(sunat: "123")),
            ("a long category", Product(category: new string('K', 101))),
        };
        foreach (var (name, body) in cases)
        {
            await ExpectRefusedAsync(name, setup.Owner.PostAsJsonAsync("/api/v1/products", body));
        }

        var created = await setup.Owner.PostAsJsonAsync("/api/v1/products", Product(code: "SKU_100", description: "Café 100% molido"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var product = (await created.Content.ReadFromJsonAsync<ProductShape>(ApiFixture.JsonOptions))!;

        await ExpectRefusedAsync("a changed code", setup.Owner.PutAsJsonAsync($"/api/v1/products/{product.Id}", Product(code: "SKU-OTHER")));
        await ExpectRefusedAsync("an update that breaks a rule", setup.Owner.PutAsJsonAsync($"/api/v1/products/{product.Id}", Product(code: "SKU_100", value: -5m)));
        await ExpectRefusedAsync("a missing product", setup.Owner.PutAsJsonAsync($"/api/v1/products/{Guid.NewGuid()}", Product()), HttpStatusCode.NotFound);
        await ExpectRefusedAsync("deactivating a missing product", setup.Owner.PostAsync($"/api/v1/products/{Guid.NewGuid()}/deactivate", null), HttpStatusCode.NotFound);

        // The search treats % and _ as plain characters, not as wildcards.
        var byPercent = (await setup.Owner.GetFromJsonAsync<List<ProductShape>>("/api/v1/products?search=100%25", ApiFixture.JsonOptions))!;
        Assert.Single(byPercent);
        Assert.Empty((await setup.Owner.GetFromJsonAsync<List<ProductShape>>("/api/v1/products?search=SKU_1_0", ApiFixture.JsonOptions))!);
        Assert.Empty((await setup.Owner.GetFromJsonAsync<List<ProductShape>>("/api/v1/products?search=%25%25%25zzz", ApiFixture.JsonOptions))!);
    }

    private sealed record ProductShape(Guid Id);

    // ---------- users ----------

    [Fact]
    public async Task The_administration_of_users_refuses_what_breaks_its_rules()
    {
        var setup = await NewTenantAsync("User Rules SAC");

        object NewUser(string email = "nuevo@example.pe", string name = "Usuario Nuevo", string[]? roles = null) =>
            new { email, displayName = name, password = "Clave-Segura-9!x", roles = roles ?? [Roles.Sales], tenantId = setup.TenantId };

        await ExpectRefusedAsync("a bad email", setup.Owner.PostAsJsonAsync("/api/v1/users", NewUser(email: "nope")));
        await ExpectRefusedAsync("a short name", setup.Owner.PostAsJsonAsync("/api/v1/users", NewUser(name: "A")));
        await ExpectRefusedAsync("no roles", setup.Owner.PostAsJsonAsync("/api/v1/users", NewUser(roles: [])));
        await ExpectRefusedAsync("an unknown role", setup.Owner.PostAsJsonAsync("/api/v1/users", NewUser(roles: ["Wizard"])));
        await ExpectRefusedAsync("platform and tenant roles together", setup.Owner.PostAsJsonAsync("/api/v1/users", NewUser(roles: [Roles.Sales, Roles.PlatformSupport])));
        await ExpectRefusedAsync("a reseller role", setup.Owner.PostAsJsonAsync("/api/v1/users", NewUser(roles: [Roles.ResellerAdmin])));

        var created = await setup.Owner.PostAsJsonAsync("/api/v1/users", NewUser());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var user = (await created.Content.ReadFromJsonAsync<UserDto>(ApiFixture.JsonOptions))!;

        // The last role stays; a role that is added can be removed again.
        await ExpectRefusedAsync("removing the last role", setup.Owner.DeleteAsync($"/api/v1/users/{user.Id}/roles/{Roles.Sales}"));
        Assert.Equal(HttpStatusCode.OK, (await setup.Owner.PostAsJsonAsync($"/api/v1/users/{user.Id}/roles", new { role = Roles.Accountant })).StatusCode);
        var removed = await setup.Owner.DeleteAsync($"/api/v1/users/{user.Id}/roles/{Roles.Sales}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal([Roles.Accountant], (await removed.Content.ReadFromJsonAsync<UserDto>(ApiFixture.JsonOptions))!.Roles);
        var notAssigned = await setup.Owner.DeleteAsync($"/api/v1/users/{user.Id}/roles/{Roles.Auditor}"); // nothing to remove: the user stays as it is
        Assert.Equal(HttpStatusCode.OK, notAssigned.StatusCode);
        Assert.Equal([Roles.Accountant], (await notAssigned.Content.ReadFromJsonAsync<UserDto>(ApiFixture.JsonOptions))!.Roles);

        // Another user's sessions can be revoked and the account deactivated; a missing user is not found.
        var revoked = await setup.Owner.PostAsync($"/api/v1/users/{user.Id}/sessions/revoke", null);
        Assert.True(revoked.IsSuccessStatusCode, await BodyAsync(revoked));
        await ExpectRefusedAsync("a missing user", setup.Owner.PostAsync($"/api/v1/users/{Guid.NewGuid()}/sessions/revoke", null), HttpStatusCode.NotFound);
        var deactivated = await setup.Owner.PostAsync($"/api/v1/users/{user.Id}/deactivate", null);
        Assert.True(deactivated.IsSuccessStatusCode, await BodyAsync(deactivated));
    }

    // ---------- billing ----------

    private static object[] Lines(decimal value = 100m) =>
        [new { description = "Servicio", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = value, igvAffectationCode = "10" } }];

    private static object Invoice(Setup setup, string? date = null, object[]? lines = null, Guid? series = null) => new
    {
        seriesId = series ?? setup.Invoice.Id,
        issueDate = date ?? Iso(TodayInLima()),
        currency = "PEN",
        buyer = new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
        lines = lines ?? Lines(),
    };

    [Fact]
    public async Task Documents_and_notes_that_break_the_rules_of_issue_are_refused_before_numbering()
    {
        var setup = await NewTenantAsync("Issue Rules SAC");

        await ExpectRefusedAsync("a bad idempotency key", PostAsync(setup.Owner, "/api/v1/documents", Invoice(setup), key: "short"), HttpStatusCode.UnprocessableEntity);
        await ExpectRefusedAsync("a date too old", PostAsync(setup.Owner, "/api/v1/documents", Invoice(setup, date: Iso(TodayInLima().AddDays(-30)))));
        await ExpectRefusedAsync("a date in the future", PostAsync(setup.Owner, "/api/v1/documents", Invoice(setup, date: Iso(TodayInLima().AddDays(2)))));
        await ExpectRefusedAsync("a line without description", PostAsync(setup.Owner, "/api/v1/documents",
            Invoice(setup, lines: [new { description = " ", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 10m, igvAffectationCode = "10" } }])));
        await ExpectRefusedAsync("a note series for a document", PostAsync(setup.Owner, "/api/v1/documents", Invoice(setup, series: setup.Credit.Id)));
        await ExpectRefusedAsync("an unknown series", PostAsync(setup.Owner, "/api/v1/documents", Invoice(setup, series: Guid.NewGuid())), HttpStatusCode.NotFound);

        var first = await PostAsync(setup.Owner, "/api/v1/documents", Invoice(setup));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var invoice = (await first.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(1, invoice.Number); // nothing refused took a number

        object Note(string reason = "01", object[]? lines = null, Guid? series = null, Guid? referenced = null) => new
        {
            seriesId = series ?? setup.Credit.Id,
            referencedDocumentId = referenced ?? invoice.Id,
            issueDate = Iso(TodayInLima()),
            reasonCode = reason,
            reason = "Ajuste",
            lines = lines ?? Lines(10m),
        };

        await ExpectRefusedAsync("a note with a bad idempotency key", PostAsync(setup.Owner, "/api/v1/notes", Note(), key: "short"));
        await ExpectRefusedAsync("a note without lines", PostAsync(setup.Owner, "/api/v1/notes", Note(lines: [])));
        await ExpectRefusedAsync("a note with an unknown reason", PostAsync(setup.Owner, "/api/v1/notes", Note(reason: "99")));
        await ExpectRefusedAsync("a note on an invoice with the series of invoices", PostAsync(setup.Owner, "/api/v1/notes", Note(series: setup.Invoice.Id)));
        await ExpectRefusedAsync("a note on a missing document", PostAsync(setup.Owner, "/api/v1/notes", Note(referenced: Guid.NewGuid())), HttpStatusCode.NotFound);
        await ExpectRefusedAsync("an installment adjustment of an invoice paid in cash", PostAsync(setup.Owner, "/api/v1/notes", Note(reason: "13", lines: null)));

        var receipt = await PostAsync(setup.Owner, "/api/v1/documents", new
        {
            seriesId = setup.Receipt.Id,
            issueDate = Iso(TodayInLima()),
            currency = "PEN",
            buyer = new { documentTypeCode = "1", documentNumber = "12345678", name = "Persona" },
            lines = Lines(50m),
        });
        Assert.Equal(HttpStatusCode.Created, receipt.StatusCode);
        var receiptDocument = (await receipt.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        await ExpectRefusedAsync("an installment adjustment of a receipt", PostAsync(setup.Owner, "/api/v1/notes", new
        {
            seriesId = setup.Credit.Id,
            referencedDocumentId = receiptDocument.Id,
            issueDate = Iso(TodayInLima()),
            reasonCode = "13",
            reason = "Ajuste de cuotas",
            installments = new[] { new { amount = 10m, dueDate = Iso(TodayInLima().AddDays(30)) } },
        }));
    }

    [Fact]
    public async Task An_inactive_series_or_company_issues_nothing_and_a_series_cannot_be_deactivated_twice_by_mistake()
    {
        var setup = await NewTenantAsync("Inactive Rules SAC");

        var invoice = (await (await PostAsync(setup.Owner, "/api/v1/documents", Invoice(setup))).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;

        Assert.True((await setup.Owner.PostAsync($"/api/v1/series/{setup.Receipt.Id}/deactivate", null)).IsSuccessStatusCode);
        Assert.True((await setup.Owner.PostAsync($"/api/v1/series/{setup.Credit.Id}/deactivate", null)).IsSuccessStatusCode);
        await ExpectRefusedAsync("an inactive series", PostAsync(setup.Owner, "/api/v1/documents", Invoice(setup, series: setup.Receipt.Id)));
        await ExpectRefusedAsync("an inactive series for a note", PostAsync(setup.Owner, "/api/v1/notes", new
        {
            seriesId = setup.Credit.Id,
            referencedDocumentId = invoice.Id,
            issueDate = Iso(TodayInLima()),
            reasonCode = "01",
            reason = "x",
            lines = Lines(10m),
        }));

        var deactivated = await setup.Owner.PostAsync($"/api/v1/companies/{setup.Company.Id}/deactivate", null);
        Assert.True(deactivated.IsSuccessStatusCode, await BodyAsync(deactivated));
        await ExpectRefusedAsync("an inactive company", PostAsync(setup.Owner, "/api/v1/documents", Invoice(setup)));
        await ExpectRefusedAsync("a series for an inactive company", setup.Owner.PostAsJsonAsync("/api/v1/series", new { companyId = setup.Company.Id, documentTypeCode = "01", code = "F009" }));
    }
}
