using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using SecureFact.Billing.Contracts;
using SecureFact.Customers.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;
using SecureFact.Products.Contracts;

namespace SecureFact.Security.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class MasterDataApiTests(ApiFixture api)
{
    private sealed record Setup(Guid TenantId, HttpClient Owner);

    private async Task<Setup> NewTenantAsync(string name)
    {
        var tenantId = await api.CreateTenantAsync(name);
        using var admin = await api.AdminClientAsync();
        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        return new Setup(tenantId, api.ClientFor(await api.LoginOkAsync(user.Email, user.Password)));
    }

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private static object CustomerBody(string type = "6", string number = "20100066603", string name = "Cliente Uno SAC") =>
        new { documentTypeCode = type, documentNumber = number, name, address = "Av. Arequipa 100", email = "compras@cliente.pe" };

    private static object ProductBody(string code = "SKU-001", string affectation = "10", decimal unitValue = 50m) => new
    {
        internalCode = code,
        description = "Servicio de instalación",
        kind = "Service",
        unitCode = "ZZ",
        unitValue,
        igvAffectationCode = affectation,
    };

    // ---------- customers ----------

    [Fact]
    public async Task A_customer_can_be_created_found_updated_and_deactivated()
    {
        var setup = await NewTenantAsync("Customers Basic SAC");

        var created = await setup.Owner.PostAsJsonAsync("/api/v1/customers", CustomerBody());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var customer = (await created.Content.ReadFromJsonAsync<CustomerDto>(ApiFixture.JsonOptions))!;
        Assert.True(customer.IsActive);

        var found = (await setup.Owner.GetFromJsonAsync<List<CustomerDto>>("/api/v1/customers?search=uno", ApiFixture.JsonOptions))!;
        Assert.Contains(found, c => c.Id == customer.Id);
        var byNumber = (await setup.Owner.GetFromJsonAsync<List<CustomerDto>>("/api/v1/customers?search=20100066", ApiFixture.JsonOptions))!;
        Assert.Contains(byNumber, c => c.Id == customer.Id);

        var update = await setup.Owner.PutAsJsonAsync($"/api/v1/customers/{customer.Id}", CustomerBody(name: "Cliente Uno Renombrado SAC"));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("Cliente Uno Renombrado SAC", (await update.Content.ReadFromJsonAsync<CustomerDto>(ApiFixture.JsonOptions))!.Name);

        Assert.Equal(HttpStatusCode.NoContent, (await setup.Owner.PostAsync($"/api/v1/customers/{customer.Id}/deactivate", null)).StatusCode);
        var active = (await setup.Owner.GetFromJsonAsync<List<CustomerDto>>("/api/v1/customers", ApiFixture.JsonOptions))!;
        Assert.DoesNotContain(active, c => c.Id == customer.Id);
        var all = (await setup.Owner.GetFromJsonAsync<List<CustomerDto>>("/api/v1/customers?includeInactive=true", ApiFixture.JsonOptions))!;
        Assert.Contains(all, c => c.Id == customer.Id && !c.IsActive);
    }

    [Theory]
    [InlineData("6", "20100066604")]
    [InlineData("1", "1234567")]
    [InlineData("1", "ABCDEFGH")]
    [InlineData("9", "123")]
    [InlineData("7", "")]
    public async Task Invalid_identity_documents_are_rejected(string type, string number)
    {
        var setup = await NewTenantAsync($"Customers Invalid {type}{number} SAC");

        var response = await setup.Owner.PostAsJsonAsync("/api/v1/customers", CustomerBody(type, number));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("SF-CUS-001", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Duplicates_are_per_tenant_and_the_identity_document_cannot_change()
    {
        var a = await NewTenantAsync("Customers Dup A SAC");
        var b = await NewTenantAsync("Customers Dup B SAC");
        var created = (await (await a.Owner.PostAsJsonAsync("/api/v1/customers", CustomerBody())).Content.ReadFromJsonAsync<CustomerDto>(ApiFixture.JsonOptions))!;

        var duplicate = await a.Owner.PostAsJsonAsync("/api/v1/customers", CustomerBody());
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("SF-CUS-003", await ProblemCodeAsync(duplicate));

        Assert.Equal(HttpStatusCode.Created, (await b.Owner.PostAsJsonAsync("/api/v1/customers", CustomerBody())).StatusCode);

        var rekey = await a.Owner.PutAsJsonAsync($"/api/v1/customers/{created.Id}", CustomerBody(type: "1", number: "12345678"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rekey.StatusCode);
    }

    [Fact]
    public async Task Customers_are_isolated_between_tenants()
    {
        var a = await NewTenantAsync("Customers Iso A SAC");
        var b = await NewTenantAsync("Customers Iso B SAC");
        var customer = (await (await a.Owner.PostAsJsonAsync("/api/v1/customers", CustomerBody())).Content.ReadFromJsonAsync<CustomerDto>(ApiFixture.JsonOptions))!;

        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/customers/{customer.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PutAsJsonAsync($"/api/v1/customers/{customer.Id}", CustomerBody(name: "Hijack SAC"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PostAsync($"/api/v1/customers/{customer.Id}/deactivate", null)).StatusCode);
        Assert.DoesNotContain((await b.Owner.GetFromJsonAsync<List<CustomerDto>>("/api/v1/customers?includeInactive=true", ApiFixture.JsonOptions))!, c => c.Id == customer.Id);
    }

    [Fact]
    public async Task Search_treats_wildcards_as_plain_text()
    {
        var setup = await NewTenantAsync("Customers Like SAC");
        var created = await setup.Owner.PostAsJsonAsync("/api/v1/customers", CustomerBody(name: "Comercial 100% Perú SAC"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var wildcard = (await setup.Owner.GetFromJsonAsync<List<CustomerDto>>("/api/v1/customers?search=%25", ApiFixture.JsonOptions))!;
        var underscore = (await setup.Owner.GetFromJsonAsync<List<CustomerDto>>("/api/v1/customers?search=_", ApiFixture.JsonOptions))!;

        Assert.Single(wildcard); // only the name that really contains '%'
        Assert.Empty(underscore);
    }

    [Fact]
    public async Task Roles_control_master_data_access()
    {
        var setup = await NewTenantAsync("MasterData Rbac SAC");
        var reader = await ApiFixture.CreateUserAsync(setup.Owner, Roles.ReadOnly, setup.TenantId);
        var sales = await ApiFixture.CreateUserAsync(setup.Owner, Roles.Sales, setup.TenantId);
        using var readerClient = api.ClientFor(await api.LoginOkAsync(reader.Email, reader.Password));
        using var salesClient = api.ClientFor(await api.LoginOkAsync(sales.Email, sales.Password));

        Assert.Equal(HttpStatusCode.OK, (await readerClient.GetAsync("/api/v1/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync("/api/v1/customers", CustomerBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync("/api/v1/products", ProductBody())).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await salesClient.PostAsJsonAsync("/api/v1/customers", CustomerBody("1", "12345678", "Persona Natural"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await salesClient.GetAsync("/api/v1/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await salesClient.PostAsJsonAsync("/api/v1/products", ProductBody())).StatusCode);
    }

    // ---------- products ----------

    [Fact]
    public async Task A_product_can_be_created_updated_and_deactivated()
    {
        var setup = await NewTenantAsync("Products Basic SAC");

        var created = await setup.Owner.PostAsJsonAsync("/api/v1/products", ProductBody());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var product = (await created.Content.ReadFromJsonAsync<ProductDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(ProductKind.Service, product.Kind);
        Assert.Equal(50m, product.UnitValue);

        var update = await setup.Owner.PutAsJsonAsync($"/api/v1/products/{product.Id}", ProductBody(unitValue: 55.1234567891m, affectation: "20"));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<ProductDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(55.1234567891m, updated.UnitValue);
        Assert.Equal("20", updated.IgvAffectationCode);

        Assert.Equal(HttpStatusCode.NoContent, (await setup.Owner.PostAsync($"/api/v1/products/{product.Id}/deactivate", null)).StatusCode);
        Assert.DoesNotContain((await setup.Owner.GetFromJsonAsync<List<ProductDto>>("/api/v1/products", ApiFixture.JsonOptions))!, p => p.Id == product.Id);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("1")]
    [InlineData("")]
    public async Task The_igv_affectation_must_exist_in_the_official_catalogue(string affectation)
    {
        var setup = await NewTenantAsync($"Products Catalog {affectation} SAC");

        var response = await setup.Owner.PostAsJsonAsync("/api/v1/products", ProductBody(affectation: affectation));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("SF-PRD-001", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Product_codes_are_unique_per_tenant_immutable_and_isolated()
    {
        var a = await NewTenantAsync("Products Iso A SAC");
        var b = await NewTenantAsync("Products Iso B SAC");
        var product = (await (await a.Owner.PostAsJsonAsync("/api/v1/products", ProductBody())).Content.ReadFromJsonAsync<ProductDto>(ApiFixture.JsonOptions))!;

        var duplicate = await a.Owner.PostAsJsonAsync("/api/v1/products", ProductBody());
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("SF-PRD-003", await ProblemCodeAsync(duplicate));
        Assert.Equal(HttpStatusCode.Created, (await b.Owner.PostAsJsonAsync("/api/v1/products", ProductBody())).StatusCode);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await a.Owner.PutAsJsonAsync($"/api/v1/products/{product.Id}", ProductBody(code: "SKU-OTHER"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/products/{product.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PutAsJsonAsync($"/api/v1/products/{product.Id}", ProductBody())).StatusCode);
    }

    [Fact]
    public async Task Product_values_follow_the_numeric_limits()
    {
        var setup = await NewTenantAsync("Products Numbers SAC");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await setup.Owner.PostAsJsonAsync("/api/v1/products", ProductBody(unitValue: -1m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await setup.Owner.PostAsJsonAsync("/api/v1/products", ProductBody(unitValue: 1.12345678901m))).StatusCode);
    }

    // ---------- master data is never hard-deleted ----------

    [Fact]
    public async Task The_runtime_role_cannot_delete_master_data()
    {
        var setup = await NewTenantAsync("MasterData Delete SAC");
        await setup.Owner.PostAsJsonAsync("/api/v1/customers", CustomerBody());

        await using var connection = new NpgsqlConnection(api.Postgres.AppConnectionString);
        await connection.OpenAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
        {
            scope.Parameters.AddWithValue("t", setup.TenantId.ToString("D"));
            await scope.ExecuteNonQueryAsync();
        }

        foreach (var sql in new[] { "DELETE FROM customers.customer", "DELETE FROM products.product" })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    // ---------- documents by customer reference ----------

    [Fact]
    public async Task A_document_can_reference_a_customer_and_keeps_a_snapshot()
    {
        var setup = await NewTenantAsync("Doc Customer SAC");
        var company = (await (await setup.Owner.PostAsJsonAsync("/api/v1/companies", new
        {
            ruc = "20100070970",
            details = new { legalName = "Emisora SAC", fiscalAddress = "Av. Larco 123", ubigeo = "150122" },
        })).Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!;
        var series = (await (await setup.Owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = "01", code = "F001" })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;
        var customer = (await (await setup.Owner.PostAsJsonAsync("/api/v1/customers", CustomerBody())).Content.ReadFromJsonAsync<CustomerDto>(ApiFixture.JsonOptions))!;

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Lima")).DateTime);
        object Body(object? buyer, Guid? customerId) => new
        {
            seriesId = series.Id,
            issueDate = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            currency = "PEN",
            buyer,
            customerId,
            lines = new[] { new { description = "Servicio", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10" } } },
        };

        async Task<HttpResponseMessage> Post(object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents") { Content = JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            return await setup.Owner.SendAsync(request);
        }

        var response = await Post(Body(null, customer.Id));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var document = (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(customer.DocumentNumber, document.Buyer.DocumentNumber);
        Assert.Equal("Cliente Uno SAC", document.Buyer.Name);

        // Editing the customer later never changes the issued document.
        await setup.Owner.PutAsJsonAsync($"/api/v1/customers/{customer.Id}", CustomerBody(name: "Nombre Posterior SAC"));
        var again = (await setup.Owner.GetFromJsonAsync<DocumentDto>($"/api/v1/documents/{document.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal("Cliente Uno SAC", again.Buyer.Name);

        // Buyer inline and by reference together (or neither) is ambiguous.
        var inline = new { documentTypeCode = "6", documentNumber = "20100066603", name = "X SAC" };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(Body(inline, customer.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(Body(null, null))).StatusCode);

        // Inactive or unknown customers cannot be used.
        await setup.Owner.PostAsync($"/api/v1/customers/{customer.Id}/deactivate", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(Body(null, customer.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(Body(null, Guid.NewGuid()))).StatusCode);
    }
}
