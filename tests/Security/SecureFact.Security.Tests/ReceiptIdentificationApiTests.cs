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
/// A receipt of more than S/ 700 must identify its buyer (rule 2514 of the daily summary sheet; the amount is the versioned rule <c>billing.receipt_identification_threshold</c>).
/// Billing refuses it when issued, because the summary that has to report it would be refused later.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ReceiptIdentificationApiTests(ApiFixture api)
{
    private static int _rucCounter = 27_000_000;

    private sealed record Setup(HttpClient Owner, SeriesDto Receipt, SeriesDto DebitOfReceipt);

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
        var company = (await (await owner.PostAsJsonAsync("/api/v1/companies", new { ruc, details = new { legalName = "Emisora SAC", fiscalAddress = "Av. Larco 123", ubigeo = "150122" } }))
            .Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/certificates", new { companyId = company.Id, pfxBase64 = PfxFor(ruc), password = "pw" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = "Sol-Clave-rec-1" })).StatusCode);

        async Task<SeriesDto> SeriesAsync(string type, string code) =>
            (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = type, code })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        return new Setup(owner, await SeriesAsync("03", "B001"), await SeriesAsync("08", "BD01"));
    }

    private static readonly object Unidentified = new { documentTypeCode = "0", documentNumber = "-", name = "Clientes varios" };
    private static readonly object Identified = new { documentTypeCode = "1", documentNumber = "12345678", name = "Persona Natural" };

    private static object[] Exempt(decimal value) =>
        [new { description = "Servicio exonerado", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = value, igvAffectationCode = "20" } }];

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> ReceiptAsync(Setup setup, object buyer, object[] lines, string currency = "PEN", string? operationType = null) =>
        PostAsync(setup.Owner, "/api/v1/documents", new { seriesId = setup.Receipt.Id, issueDate = Iso(TodayInLima()), currency, buyer, lines, operationTypeCode = operationType });

    private static string ProblemCode(string json)
    {
        using var body = JsonDocument.Parse(json);
        return body.RootElement.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task A_receipt_of_more_than_700_soles_must_identify_the_buyer_and_nothing_refused_takes_a_number()
    {
        var setup = await NewTenantAsync("Receipt Threshold SAC");

        var over = await ReceiptAsync(setup, Unidentified, Exempt(700.01m));
        var taxed = await ReceiptAsync(setup, Unidentified, [new { description = "Servicio", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 600m, igvAffectationCode = "10" } }]); // 708.00
        var export = await ReceiptAsync(setup, Unidentified, [new { description = "Bien", unitCode = "NIU", tax = new { quantity = 1m, unitValue = 800m, igvAffectationCode = "40" } }], operationType: "0200");

        foreach (var (name, response) in new[] { ("700.01", over), ("708.00 taxed", taxed), ("an export in soles", export) })
        {
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{name}: {response.StatusCode}");
            var text = await response.Content.ReadAsStringAsync();
            Assert.Equal("SF-BIL-006", ProblemCode(text));
            Assert.Contains("Adquirente sin identificar", text, StringComparison.Ordinal);
        }

        var exact = await ReceiptAsync(setup, Unidentified, Exempt(700m)); // not above the threshold
        Assert.Equal(HttpStatusCode.Created, exact.StatusCode);
        Assert.Equal(1, (await exact.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!.Number);
    }

    [Fact]
    public async Task The_threshold_does_not_apply_to_an_identified_buyer_or_to_another_currency()
    {
        var setup = await NewTenantAsync("Receipt Threshold Exceptions SAC");

        Assert.Equal(HttpStatusCode.Created, (await ReceiptAsync(setup, Identified, Exempt(5000m))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ReceiptAsync(setup, new { documentTypeCode = "7", documentNumber = "P1234567", name = "Turista" }, Exempt(5000m))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ReceiptAsync(setup, Unidentified, Exempt(5000m), "USD")).StatusCode);
    }

    [Fact]
    public async Task A_debit_note_of_a_receipt_is_held_to_the_same_threshold()
    {
        var setup = await NewTenantAsync("Receipt Debit Threshold SAC");
        var receipt = (await (await ReceiptAsync(setup, Unidentified, Exempt(100m))).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;

        Task<HttpResponseMessage> Debit(decimal value) => PostAsync(setup.Owner, "/api/v1/notes", new
        {
            seriesId = setup.DebitOfReceipt.Id,
            referencedDocumentId = receipt.Id,
            issueDate = Iso(TodayInLima()),
            reasonCode = "01",
            reason = "Intereses por mora",
            lines = Exempt(value),
        });

        var over = await Debit(701m);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        Assert.Contains("Adquirente sin identificar", await over.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Created, (await Debit(700m)).StatusCode);
    }
}
