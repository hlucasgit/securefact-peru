using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>
/// A detraction on a debit note (ADR-049): the sheet NotaDebito2_0 of SUNAT has a detraction section and the one of the credit note has none, so only a debit note on an invoice carries it.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class NoteDetractionApiTests(ApiFixture api)
{
    private static int _rucCounter = 29_000_000;

    private sealed record Setup(HttpClient Owner, CompanyDto Company, SeriesDto Invoice, SeriesDto Receipt, SeriesDto Credit, SeriesDto Debit, SeriesDto DebitOfReceipt);

    private static string NewRuc()
    {
        var body = "20" + Interlocked.Increment(ref _rucCounter).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var sum = body.Select((c, i) => (c - '0') * weights[i]).Sum();
        return body + ((11 - (sum % 11)) % 10);
    }

    private static string Today() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Lima")).DateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

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

        async Task<SeriesDto> SeriesAsync(string type, string code) =>
            (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = type, code })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        return new Setup(owner, company, await SeriesAsync("01", "F001"), await SeriesAsync("03", "B001"), await SeriesAsync("07", "FC01"), await SeriesAsync("08", "FD01"), await SeriesAsync("08", "BD01"));
    }

    private static async Task<DocumentDto> IssueAsync(HttpClient client, SeriesDto series, bool receipt, string currency = "PEN")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
        {
            Content = JsonContent.Create(new
            {
                seriesId = series.Id,
                issueDate = Today(),
                currency,
                buyer = receipt
                    ? new { documentTypeCode = "1", documentNumber = "12345678", name = "Persona Natural" }
                    : new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
                lines = new[] { new { description = "Servicio de consultoría", unitCode = "ZZ", tax = new { quantity = 2m, unitValue = 100m, igvAffectationCode = "10" } } },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
    }

    // 5 x 100 + 18 % = 590.00; 12 % of it is 70.80
    private static object Note(SeriesDto series, DocumentDto referenced, object? detraction, string reason = "02", decimal quantity = 5m) => new
    {
        seriesId = series.Id,
        referencedDocumentId = referenced.Id,
        issueDate = Today(),
        reasonCode = reason,
        reason = "Aumento en el valor del servicio",
        lines = new[] { new { description = "Servicio de consultoría", unitCode = "ZZ", tax = new { quantity, unitValue = 100m, igvAffectationCode = "10" } } },
        detraction,
    };

    private static object Detraction(decimal amount = 70.80m, string account = "00012345678") => new { goodsOrServiceCode = "037", percentage = 12m, amount, accountNumber = account };

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, object body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notes") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return client.SendAsync(request);
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task A_debit_note_on_an_invoice_carries_its_detraction_into_the_document_and_the_xml()
    {
        var setup = await NewTenantAsync("Nota con detracción SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);

        var created = await PostAsync(setup.Owner, Note(setup.Debit, invoice, Detraction()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var note = (await created.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(("037", 12m, 70.80m, "00012345678"), (note.Detraction!.GoodsOrServiceCode, note.Detraction.Percentage, note.Detraction.Amount, note.Detraction.AccountNumber));
        Assert.Equal(OperationTypes.Sale, note.OperationTypeCode); // a note has no operation type: the detraction does not give it one
        Assert.Equal(590.00m, note.Totals.PayableAmount);

        var electronic = await (await setup.Owner.PostAsync($"/api/v1/documents/{note.Id}/electronic", null)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions);
        var xml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic!.Id}/xml");
        Assert.Contains("<DebitNote", xml, StringComparison.Ordinal);
        Assert.Matches("<cbc:ID>Detraccion</cbc:ID>\\s*<cbc:PaymentMeansCode[^>]*>001</cbc:PaymentMeansCode>\\s*<cac:PayeeFinancialAccount>\\s*<cbc:ID>00012345678</cbc:ID>", xml);
        Assert.Matches("<cac:PaymentTerms>\\s*<cbc:ID>Detraccion</cbc:ID>\\s*<cbc:PaymentMeansID[^>]*>037</cbc:PaymentMeansID>\\s*<cbc:PaymentPercent>12.00</cbc:PaymentPercent>\\s*<cbc:Amount currencyID=\"PEN\">70.80</cbc:Amount>", xml);

        // The document that the API serves later still says so, and the printed form states it.
        var again = (await setup.Owner.GetFromJsonAsync<DocumentDto>($"/api/v1/documents/{note.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal(70.80m, again.Detraction!.Amount);
        var pdf = await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf");
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public async Task A_credit_note_never_carries_a_detraction_and_a_debit_note_only_on_an_invoice_in_soles()
    {
        var setup = await NewTenantAsync("Reglas de la nota SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var receipt = await IssueAsync(setup.Owner, setup.Receipt, receipt: true);
        var dollars = await IssueAsync(setup.Owner, setup.Invoice, receipt: false, currency: "USD");

        // The sheet of the credit note has no detraction section.
        var credit = await PostAsync(setup.Owner, Note(setup.Credit, invoice, Detraction(amount: 28.32m), reason: "07", quantity: 1m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, credit.StatusCode);
        Assert.Equal("SF-BIL-006", await CodeAsync(credit));
        Assert.Contains("nota de crédito", await credit.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal("SF-BIL-006", await CodeAsync(await PostAsync(setup.Owner, Note(setup.DebitOfReceipt, receipt, Detraction())))); // on a receipt
        Assert.Equal("SF-BIL-006", await CodeAsync(await PostAsync(setup.Owner, Note(setup.Debit, dollars, Detraction())))); // not in soles
    }

    [Fact]
    public async Task The_data_of_the_detraction_of_a_note_are_checked_as_in_an_invoice()
    {
        var setup = await NewTenantAsync("Datos de la nota SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);

        foreach (var bad in new object[]
        {
            Detraction(amount: 5m), // far from 12 % of 590.00
            Detraction(amount: 0m),
            Detraction(amount: 700m),
            new { goodsOrServiceCode = "999", percentage = 12m, amount = 70.80m, accountNumber = "00012345678" }, // not in catalogue 54
            new { goodsOrServiceCode = "037", percentage = 0m, amount = 70.80m, accountNumber = "00012345678" },
            new { goodsOrServiceCode = "037", percentage = 12m, amount = 70.80m, accountNumber = (string?)null }, // the company has no account either
        })
        {
            var refused = await PostAsync(setup.Owner, Note(setup.Debit, invoice, bad));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("SF-BIL-006", await CodeAsync(refused));
        }

        // A note without detraction is not affected, and a repeated request answers with the original note.
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(setup.Owner, Note(setup.Debit, invoice, null))).StatusCode);
        var key = Guid.NewGuid().ToString("N");
        var first = await PostAsync(setup.Owner, Note(setup.Debit, invoice, Detraction()), key);
        var second = await PostAsync(setup.Owner, Note(setup.Debit, invoice, Detraction()), key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!.Id, (await second.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!.Id);
    }

    [Fact]
    public async Task The_preview_of_a_debit_note_gives_its_total_and_the_suggested_detraction_and_numbers_nothing()
    {
        var setup = await NewTenantAsync("Vista de nota SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);

        async Task<HttpResponseMessage> PreviewAsync(object note, decimal? percentage = null) =>
            await setup.Owner.PostAsJsonAsync("/api/v1/notes/preview", new { note, detractionPercentage = percentage });

        var preview = await PreviewAsync(Note(setup.Debit, invoice, null), 12m);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var body = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.Equal(590.00m, body.RootElement.GetProperty("totals").GetProperty("payableAmount").GetDecimal());
        Assert.Equal(70.80m, body.RootElement.GetProperty("detractionAmount").GetDecimal());
        Assert.Equal(519.20m, body.RootElement.GetProperty("netPendingAmount").GetDecimal());

        // A detraction already typed with a wrong amount does not stop the preview, and the rules of the note are the same as in the issue.
        Assert.Equal(HttpStatusCode.OK, (await PreviewAsync(Note(setup.Debit, invoice, Detraction(amount: 1m)), 12m)).StatusCode);
        var credit = await PreviewAsync(Note(setup.Credit, invoice, Detraction(amount: 28.32m), reason: "07", quantity: 1m), 12m);
        Assert.Equal("SF-BIL-006", await CodeAsync(credit));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PreviewAsync(Note(setup.Debit, invoice, null), 0m)).StatusCode);

        // Nothing was numbered: the first real note takes the number 1.
        var created = await PostAsync(setup.Owner, Note(setup.Debit, invoice, Detraction()));
        Assert.Equal(1, (await created.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!.Number);
    }

    [Fact]
    public async Task A_note_without_its_own_account_uses_the_one_of_the_company_and_keeps_it()
    {
        var setup = await NewTenantAsync("Cuenta de la empresa SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var details = new { legalName = "Emisora SAC", fiscalAddress = "Av. Larco 123", ubigeo = "150122", detractionAccount = "00099887766" };
        Assert.Equal(HttpStatusCode.OK, (await setup.Owner.PutAsJsonAsync($"/api/v1/companies/{setup.Company.Id}", details)).StatusCode);

        var created = await PostAsync(setup.Owner, Note(setup.Debit, invoice, new { goodsOrServiceCode = "037", percentage = 12m, amount = 70.80m }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var note = (await created.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal("00099887766", note.Detraction!.AccountNumber);

        // A later change of the account of the company does not alter the note that was issued.
        await setup.Owner.PutAsJsonAsync($"/api/v1/companies/{setup.Company.Id}", new { details.legalName, details.fiscalAddress, details.ubigeo, detractionAccount = "00011122233" });
        Assert.Equal("00099887766", (await setup.Owner.GetFromJsonAsync<DocumentDto>($"/api/v1/documents/{note.Id}", ApiFixture.JsonOptions))!.Detraction!.AccountNumber);
    }
}
