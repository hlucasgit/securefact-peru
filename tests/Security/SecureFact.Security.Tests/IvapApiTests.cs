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

/// <summary>Operations taxed with the IVAP (rice, affectation 17, tax 1016) and the credit note of reason 12 that adjusts them, end to end.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class IvapApiTests(ApiFixture api)
{
    private static int _rucCounter = 24_000_000;

    private sealed record Setup(Guid TenantId, HttpClient Owner, CompanyDto Company, SeriesDto Invoice, SeriesDto Receipt, SeriesDto CreditOfInvoice);

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
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = "Sol-Clave-ivap-1" })).StatusCode);

        async Task<SeriesDto> SeriesAsync(string type, string code) =>
            (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = type, code })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        return new Setup(tenantId, owner, company, await SeriesAsync("01", "F001"), await SeriesAsync("03", "B001"), await SeriesAsync("07", "FC01"));
    }

    private static object[] Lines(string affectation, decimal quantity = 1m) =>
        [new { description = "Arroz pilado", unitCode = "KGM", tax = new { quantity, unitValue = 100m, igvAffectationCode = affectation } }];

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    private static async Task<DocumentDto> IssueAsync(HttpClient client, SeriesDto series, bool receipt, string affectation, decimal quantity = 1m)
    {
        var response = await PostAsync(client, "/api/v1/documents", new
        {
            seriesId = series.Id,
            issueDate = Iso(TodayInLima()),
            currency = "PEN",
            buyer = receipt
                ? new { documentTypeCode = "1", documentNumber = "12345678", name = "Persona Natural" }
                : new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
            lines = Lines(affectation, quantity),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
    }

    private static Task<HttpResponseMessage> NoteAsync(Setup setup, DocumentDto referenced, string reason, string affectation, decimal quantity = 1m) =>
        PostAsync(setup.Owner, "/api/v1/notes", new
        {
            seriesId = setup.CreditOfInvoice.Id,
            referencedDocumentId = referenced.Id,
            issueDate = Iso(TodayInLima()),
            reasonCode = reason,
            reason = "Ajuste de la operación",
            lines = Lines(affectation, quantity),
        });

    private static async Task<ElectronicDocumentDto> PrepareAsync(HttpClient client, Guid documentId)
    {
        var response = await client.PostAsync($"/api/v1/documents/{documentId}/electronic", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
    }

    private async Task<ElectronicDocumentDto> AcceptAsync(Setup setup, DocumentDto document)
    {
        var electronic = await PrepareAsync(setup.Owner, document.Id);
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

    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    [Fact]
    public async Task An_invoice_taxed_with_the_ivap_is_calculated_stated_signed_accepted_and_printed()
    {
        var setup = await NewTenantAsync("Ivap Invoice SAC");

        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false, "17");

        Assert.Equal(4m, invoice.Totals.TotalIvap);
        Assert.Equal(0m, invoice.Totals.TotalIgv);
        Assert.Equal(104m, invoice.Totals.PayableAmount);
        var electronic = await AcceptAsync(setup, invoice);
        var xml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml");
        Assert.True(new XmlDsigSigner().Verify(xml).Value.IsValid);
        var root = XDocument.Parse(xml).Root!;
        Assert.Equal("2007", root.Element(Cbc + "Note")!.Attribute("languageLocaleID")!.Value);
        var subtotal = root.Element(Cac + "TaxTotal")!.Element(Cac + "TaxSubtotal")!;
        Assert.Equal("1016", subtotal.Element(Cac + "TaxCategory")!.Element(Cac + "TaxScheme")!.Element(Cbc + "ID")!.Value);
        var lineCategory = root.Element(Cac + "InvoiceLine")!.Element(Cac + "TaxTotal")!.Element(Cac + "TaxSubtotal")!.Element(Cac + "TaxCategory")!;
        Assert.Equal("4.00", lineCategory.Element(Cbc + "Percent")!.Value);
        Assert.Equal("17", lineCategory.Element(Cbc + "TaxExemptionReasonCode")!.Value);

        var pdf = PdfContent(await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf"));
        Assert.Contains("(IVAP)", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_receipt_taxed_with_the_ivap_goes_into_the_daily_summary_with_tax_1016()
    {
        var setup = await NewTenantAsync("Ivap Receipt SAC");
        await IssueAsync(setup.Owner, setup.Receipt, receipt: true, "17");

        var created = await setup.Owner.PostAsJsonAsync("/api/v1/summaries", new { companyId = setup.Company.Id, referenceDate = Iso(TodayInLima()) });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var summary = Assert.Single((await created.Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);
        var xml = XDocument.Parse(await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{summary.Document.Id}/xml"));
        XNamespace sac = "urn:sunat:names:specification:ubl:peru:schema:xsd:SunatAggregateComponents-1";
        var line = xml.Descendants(sac + "SummaryDocumentsLine").Single();
        Assert.Equal("104.00", line.Element(sac + "TotalAmount")!.Value);
        var category = line.Element(Cac + "TaxTotal")!.Element(Cac + "TaxSubtotal")!.Element(Cac + "TaxCategory")!;
        Assert.Equal("1016", category.Element(Cac + "TaxScheme")!.Element(Cbc + "ID")!.Value);
        Assert.Equal("4.00", category.Element(Cbc + "Percent")!.Value);
        Assert.Equal("100.00", line.Element(sac + "BillingPayment")!.Element(Cbc + "PaidAmount")!.Value);
    }

    [Fact]
    public async Task A_credit_note_of_reason_12_adjusts_an_ivap_invoice_and_is_accepted()
    {
        var setup = await NewTenantAsync("Ivap Adjustment SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false, "17", quantity: 2m);

        var response = await NoteAsync(setup, invoice, "12", "17");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var note = (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(4m, note.Totals.TotalIvap);
        Assert.Equal(104m, note.Totals.PayableAmount);
        Assert.Equal("12", note.Note!.ReasonCode);

        await AcceptAsync(setup, invoice);
        var electronic = await AcceptAsync(setup, note);
        var xml = XDocument.Parse(await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml")).Root!;
        Assert.Equal("12", xml.Element(Cac + "DiscrepancyResponse")!.Element(Cbc + "ResponseCode")!.Value);
        Assert.Equal("2007", xml.Element(Cbc + "Note")!.Attribute("languageLocaleID")!.Value);
    }

    [Fact]
    public async Task Ivap_notes_that_break_the_rules_are_refused_and_the_credit_is_accumulated()
    {
        var setup = await NewTenantAsync("Ivap Rules SAC");
        var rice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false, "17"); // 104.00
        var plain = await IssueAsync(setup.Owner, setup.Invoice, receipt: false, "10");

        var cases = new (string Name, Task<HttpResponseMessage> Response)[]
        {
            ("reason 12 on an IGV document", NoteAsync(setup, plain, "12", "17")),
            ("reason 12 with IGV lines", NoteAsync(setup, rice, "12", "10")),
            ("IVAP lines on reason 01", NoteAsync(setup, rice, "01", "17")),
            ("IVAP lines on reason 07", NoteAsync(setup, rice, "07", "17")),
        };
        foreach (var (name, pending) in cases)
        {
            var response = await pending;
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{name}: {response.StatusCode}");
            Assert.Equal("SF-BIL-006", ProblemCode(await response.Content.ReadAsStringAsync()));
        }

        // The note of reason 12 credits the document once: the second one would credit 208.00 of 104.00.
        Assert.Equal(HttpStatusCode.Created, (await NoteAsync(setup, rice, "12", "17")).StatusCode);
        var second = await NoteAsync(setup, rice, "12", "17");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("SF-BIL-010", ProblemCode(await second.Content.ReadAsStringAsync()));
    }
}
