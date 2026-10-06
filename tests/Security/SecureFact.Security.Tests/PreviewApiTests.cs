using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecureFact.Billing.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>The preview of a document (ADR-046): what issuing would calculate, with the same checks, and nothing issued.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class PreviewApiTests(ApiFixture api)
{
    private static int _rucCounter = 28_000_000;

    private sealed record Setup(Guid TenantId, HttpClient Owner, SeriesDto Invoice, SeriesDto Receipt);

    private static string NewRuc()
    {
        var body = "20" + Interlocked.Increment(ref _rucCounter).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var sum = body.Select((c, i) => (c - '0') * weights[i]).Sum();
        return body + ((11 - (sum % 11)) % 10);
    }

    private static string Today() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Lima")).DateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<Setup> NewTenantAsync(string name, Guid? planId = null)
    {
        var tenantId = await api.CreateTenantAsync(name);
        using var admin = await api.AdminClientAsync();
        if (planId is { } plan)
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/plan", new { planId = plan })).StatusCode);
        }

        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        var owner = api.ClientFor(await api.LoginOkAsync(user.Email, user.Password));
        var company = (await (await owner.PostAsJsonAsync("/api/v1/companies", new { ruc = NewRuc(), details = new { legalName = "Emisora SAC", fiscalAddress = "Av. Larco 123", ubigeo = "150122" } }))
            .Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!;

        async Task<SeriesDto> SeriesAsync(string type, string code) =>
            (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = type, code })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        return new Setup(tenantId, owner, await SeriesAsync("01", "F001"), await SeriesAsync("03", "B001"));
    }

    private static object Document(Setup setup, string? operationType = null, object? buyer = null, string affectation = "10", object? detraction = null) => new
    {
        seriesId = setup.Invoice.Id,
        issueDate = Today(),
        currency = "PEN",
        buyer = buyer ?? new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
        lines = new[] { new { description = "Servicio de consultoría", unitCode = "ZZ", tax = new { quantity = 2m, unitValue = 100m, igvAffectationCode = affectation } } },
        operationTypeCode = operationType,
        detraction,
    };

    private static Task<HttpResponseMessage> PreviewAsync(HttpClient client, object document, decimal? detraction = null, decimal? retention = null) =>
        client.PostAsJsonAsync("/api/v1/documents/preview", new { document, detractionPercentage = detraction, retentionPercentage = retention });

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private static async Task<decimal> PayableAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("totals").GetProperty("payableAmount").GetDecimal();
    }

    [Fact]
    public async Task The_preview_calculates_what_issuing_calculates_and_issues_nothing()
    {
        var setup = await NewTenantAsync("Vista previa SAC");

        var preview = await PreviewAsync(setup.Owner, Document(setup));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(236.00m, await PayableAsync(preview));

        // Nothing was numbered: the first real document takes the number 1, and its total is the one the preview gave.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents") { Content = JsonContent.Create(Document(setup)) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var issued = await setup.Owner.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        var document = (await issued.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(1, document.Number);
        Assert.Equal(236.00m, document.Totals.PayableAmount);
    }

    [Fact]
    public async Task The_amounts_of_a_detraction_and_of_a_retention_come_from_the_server_and_do_not_depend_on_what_was_typed()
    {
        var setup = await NewTenantAsync("Montos SAC");

        var both = await PreviewAsync(setup.Owner, Document(setup), detraction: 12m, retention: 3m);
        using var body = JsonDocument.Parse(await both.Content.ReadAsStringAsync());
        Assert.Equal(28.32m, body.RootElement.GetProperty("detractionAmount").GetDecimal()); // 12 % of 236.00
        Assert.Equal(7.08m, body.RootElement.GetProperty("retentionAmount").GetDecimal()); // 3 % of 236.00, to the cent

        var none = await PreviewAsync(setup.Owner, Document(setup));
        using var empty = JsonDocument.Parse(await none.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, empty.RootElement.GetProperty("detractionAmount").ValueKind);

        // A detraction already typed in the document, with an amount that is wrong for now, does not stop the total from being calculated.
        var typed = await PreviewAsync(setup.Owner, Document(setup, detraction: new { goodsOrServiceCode = "037", percentage = 12m, amount = 1m }), detraction: 12m);
        Assert.Equal(HttpStatusCode.OK, typed.StatusCode);
        foreach (var bad in new decimal[] { 0m, -1m, 100.01m })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PreviewAsync(setup.Owner, Document(setup), detraction: bad)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PreviewAsync(setup.Owner, Document(setup), retention: 100m)).StatusCode);
    }

    [Fact]
    public async Task The_preview_refuses_what_issuing_refuses_with_the_same_code()
    {
        var setup = await NewTenantAsync("Mismas reglas SAC");
        var domestic = new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" };

        // An export to a buyer with RUC, a line that is not an export in an export, an unknown series.
        var refusedBuyer = await PreviewAsync(setup.Owner, Document(setup, operationType: "0200", affectation: "40", buyer: domestic));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refusedBuyer.StatusCode);
        Assert.Equal("SF-BIL-006", await CodeAsync(refusedBuyer));
        Assert.Equal("SF-BIL-006", await CodeAsync(await PreviewAsync(setup.Owner, Document(setup, operationType: "0200", buyer: new { documentTypeCode = "7", documentNumber = "P1234567", name = "Foreign Buyer Ltd" }))));

        var abroad = new { documentTypeCode = "7", documentNumber = "P1234567", name = "Foreign Buyer Ltd" };
        var export = await PreviewAsync(setup.Owner, Document(setup, operationType: "0200", affectation: "40", buyer: abroad));
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);

        var unknown = await setup.Owner.PostAsJsonAsync("/api/v1/documents/preview", new { document = new { seriesId = Guid.NewGuid(), issueDate = Today(), currency = "PEN", buyer = domestic, lines = new[] { new { description = "x", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 1m, igvAffectationCode = "10" } } } } });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await setup.Owner.PostAsJsonAsync("/api/v1/documents/preview", new { document = new { seriesId = setup.Invoice.Id, issueDate = Today(), currency = "PEN", buyer = domestic, lines = Array.Empty<object>() } })).StatusCode);
    }

    [Fact]
    public async Task The_preview_needs_no_idempotency_key_takes_nothing_from_the_plan_and_is_for_those_who_issue()
    {
        using var admin = await api.AdminClientAsync();
        var created = await admin.PostAsJsonAsync("/api/v1/platform/plans", new { code = $"pv-{Guid.NewGuid():N}"[..12], name = "Un comprobante", maxDocumentsPerMonth = 1 });
        var plan = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var setup = await NewTenantAsync("Plan de uno SAC", plan);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await PreviewAsync(setup.Owner, Document(setup))).StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents") { Content = JsonContent.Create(Document(setup)) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Created, (await setup.Owner.SendAsync(request)).StatusCode); // the previews took nothing from the allowance

        var reader = await ApiFixture.CreateUserAsync(admin, Roles.ReadOnly, setup.TenantId);
        using var readOnly = api.ClientFor(await api.LoginOkAsync(reader.Email, reader.Password));
        Assert.Equal(HttpStatusCode.Forbidden, (await PreviewAsync(readOnly, Document(setup))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PreviewAsync(admin, Document(setup))).StatusCode); // platform staff are in no tenant
        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PreviewAsync(anonymous, Document(setup))).StatusCode);

        // Another tenant's series is not found: the preview reads only what the tenant sees.
        var other = await NewTenantAsync("Otra cuenta SAC");
        Assert.Equal(HttpStatusCode.NotFound, (await PreviewAsync(other.Owner, Document(setup))).StatusCode);
    }
}
