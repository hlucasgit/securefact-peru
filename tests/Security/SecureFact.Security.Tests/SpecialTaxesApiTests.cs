using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml.Linq;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>The ISC (tax 2000) and the plastic bag tax (ICBPER, tax 7152) end to end: Billing calculates, the XML states them, SUNAT accepts them, and the summary and the PDF carry them.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class SpecialTaxesApiTests(ApiFixture api)
{
    private static int _rucCounter = 31_000_000;

    private sealed record Setup(HttpClient Owner, CompanyDto Company, SeriesDto Invoice, SeriesDto Receipt, SeriesDto CreditOfInvoice);

    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Sac = "urn:sunat:names:specification:ubl:peru:schema:xsd:SunatAggregateComponents-1";

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
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = "Sol-Clave-special-1" })).StatusCode);

        async Task<SeriesDto> SeriesAsync(string type, string code) =>
            (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = type, code })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        return new Setup(owner, company, await SeriesAsync("01", "F001"), await SeriesAsync("03", "B001"), await SeriesAsync("07", "FC01"));
    }

    /// <summary>Three units of 100 with an ISC of 10 % and, when asked, three plastic bags.</summary>
    private static object[] Lines(int bags, int quantity = 3) =>
        [new { description = "Bebida en bolsa", unitCode = "NIU", tax = new { quantity, unitValue = 100m, igvAffectationCode = "10", isc = new { system = 1, rateOrUnitAmount = 0.10m }, plasticBagCount = bags } }];

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> IssueRawAsync(Setup setup, bool receipt, object[] lines) =>
        PostAsync(setup.Owner, "/api/v1/documents", new
        {
            seriesId = receipt ? setup.Receipt.Id : setup.Invoice.Id,
            issueDate = Iso(TodayInLima()),
            currency = "PEN",
            buyer = receipt
                ? new { documentTypeCode = "1", documentNumber = "12345678", name = "Persona Natural" }
                : new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
            lines,
        });

    private static async Task<DocumentDto> IssueAsync(Setup setup, bool receipt, int bags)
    {
        var response = await IssueRawAsync(setup, receipt, Lines(bags));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
    }

    private async Task<ElectronicDocumentDto> AcceptAsync(Setup setup, DocumentDto document)
    {
        var prepared = await setup.Owner.PostAsync($"/api/v1/documents/{document.Id}/electronic", null);
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var electronic = (await prepared.Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, $"{document.Series}-{document.Number}")));
        var sent = (await (await setup.Owner.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/send", null)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(EDocumentState.Accepted, sent.State);
        return sent;
    }

    private static string PdfContent(byte[] pdf)
    {
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        var content = new System.Text.StringBuilder();
        foreach (System.Text.RegularExpressions.Match stream in System.Text.RegularExpressions.Regex.Matches(text, "stream\\r?\\n(?<body>.*?)\\r?\\nendstream", System.Text.RegularExpressions.RegexOptions.Singleline, TimeSpan.FromSeconds(5)))
        {
            try
            {
                using var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(System.Text.Encoding.Latin1.GetBytes(stream.Groups["body"].Value)), System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(zlib, System.Text.Encoding.Latin1);
                content.Append(reader.ReadToEnd());
            }
            catch (InvalidDataException)
            {
                // an image or another binary stream
            }
        }

        return content.ToString();
    }

    private static string ProblemCode(string json)
    {
        using var body = JsonDocument.Parse(json);
        return body.RootElement.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task An_invoice_with_isc_and_plastic_bags_is_calculated_stated_signed_accepted_and_printed()
    {
        var setup = await NewTenantAsync("Special Taxes Invoice SAC");

        var invoice = await IssueAsync(setup, receipt: false, bags: 3);

        // Value 300, ISC 30 (10 %), IGV 18 % of 330 = 59.40, three bags at 0.50 = 1.50.
        Assert.Equal(30m, invoice.Totals.TotalIsc);
        Assert.Equal(59.4m, invoice.Totals.TotalIgv);
        Assert.Equal(1.5m, invoice.Totals.TotalIcbper);
        Assert.Equal(390.9m, invoice.Totals.PayableAmount);
        var line = Assert.Single(invoice.Lines);
        Assert.Equal(3, line.PlasticBagCount);
        Assert.Equal(0.10m, line.Isc!.RateOrUnitAmount);

        var electronic = await AcceptAsync(setup, invoice);
        var xml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml");
        Assert.True(new XmlDsigSigner().Verify(xml).Value.IsValid);
        var root = XDocument.Parse(xml).Root!;
        var lineTaxes = root.Element(Cac + "InvoiceLine")!.Element(Cac + "TaxTotal")!.Elements(Cac + "TaxSubtotal").ToList();
        Assert.Equal(["1000", "2000", "7152"], lineTaxes.Select(t => t.Element(Cac + "TaxCategory")!.Element(Cac + "TaxScheme")!.Element(Cbc + "ID")!.Value));
        Assert.Equal("0.50", lineTaxes[2].Element(Cac + "TaxCategory")!.Element(Cbc + "PerUnitAmount")!.Value);
        Assert.Equal("3", lineTaxes[2].Element(Cbc + "BaseUnitMeasure")!.Value);
        Assert.Equal("390.90", root.Element(Cac + "LegalMonetaryTotal")!.Element(Cbc + "PayableAmount")!.Value);

        var pdf = PdfContent(await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf"));
        Assert.Contains("(ISC)", pdf, StringComparison.Ordinal);
        Assert.Contains("(ICBPER)", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_receipt_with_isc_and_plastic_bags_goes_into_the_daily_summary_with_each_tax()
    {
        var setup = await NewTenantAsync("Special Taxes Receipt SAC");
        await IssueAsync(setup, receipt: true, bags: 3);

        var created = await setup.Owner.PostAsJsonAsync("/api/v1/summaries", new { companyId = setup.Company.Id, referenceDate = Iso(TodayInLima()) });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var summary = Assert.Single((await created.Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);
        var xml = XDocument.Parse(await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{summary.Document.Id}/xml"));
        var line = xml.Descendants(Sac + "SummaryDocumentsLine").Single();
        Assert.Equal("390.90", line.Element(Sac + "TotalAmount")!.Value);
        Assert.Equal("300.00", line.Element(Sac + "BillingPayment")!.Element(Cbc + "PaidAmount")!.Value);
        var taxes = line.Elements(Cac + "TaxTotal").ToList();
        Assert.Equal(["1000", "2000", "7152"], taxes.Select(t => t.Element(Cac + "TaxSubtotal")!.Element(Cac + "TaxCategory")!.Element(Cac + "TaxScheme")!.Element(Cbc + "ID")!.Value));
        Assert.Equal(["59.40", "30.00", "1.50"], taxes.Select(t => t.Element(Cbc + "TaxAmount")!.Value));
    }

    [Fact]
    public async Task A_credit_note_repeats_the_special_taxes_of_the_invoice_and_is_accepted()
    {
        var setup = await NewTenantAsync("Special Taxes Note SAC");
        var invoice = await IssueAsync(setup, receipt: false, bags: 3);
        await AcceptAsync(setup, invoice);

        var response = await PostAsync(setup.Owner, "/api/v1/notes", new
        {
            seriesId = setup.CreditOfInvoice.Id,
            referencedDocumentId = invoice.Id,
            issueDate = Iso(TodayInLima()),
            reasonCode = "01",
            reason = "Anulación de la operación",
            lines = Lines(bags: 3),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var note = (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(30m, note.Totals.TotalIsc);
        Assert.Equal(1.5m, note.Totals.TotalIcbper);
        var electronic = await AcceptAsync(setup, note);
        var root = XDocument.Parse(await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml")).Root!;
        Assert.Equal(["1000", "2000", "7152"], root.Elements(Cac + "TaxTotal").Single().Elements(Cac + "TaxSubtotal").Select(t => t.Element(Cac + "TaxCategory")!.Element(Cac + "TaxScheme")!.Element(Cbc + "ID")!.Value));
    }

    [Fact]
    public async Task Plastic_bags_that_are_not_as_many_as_the_units_of_the_line_are_refused()
    {
        var setup = await NewTenantAsync("Special Taxes Rules SAC");

        // Rule 3236: the bags of the line are its units; they also have to fit the five digits of the field (rule 2892).
        foreach (var lines in new[] { Lines(bags: 2), Lines(bags: 100_000, quantity: 100_000) })
        {
            var response = await IssueRawAsync(setup, receipt: false, lines);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("SF-BIL-006", ProblemCode(await response.Content.ReadAsStringAsync()));
        }
    }
}
