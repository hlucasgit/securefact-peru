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

/// <summary>Export of goods (operation type 0200, affectation 40, tax 9995) and the credit note of reason 11 that adjusts it, end to end.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ExportApiTests(ApiFixture api)
{
    private static int _rucCounter = 25_000_000;

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
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = "Sol-Clave-exp-1" })).StatusCode);

        async Task<SeriesDto> SeriesAsync(string type, string code) =>
            (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = type, code })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        return new Setup(tenantId, owner, company, await SeriesAsync("01", "F001"), await SeriesAsync("03", "B001"), await SeriesAsync("07", "FC01"));
    }

    private static object[] Lines(string affectation, decimal quantity = 2m) =>
        [new { description = "Bien de exportación", unitCode = "NIU", tax = new { quantity, unitValue = 50m, igvAffectationCode = affectation } }];

    private static readonly object ForeignBuyer = new { documentTypeCode = "0", documentNumber = "-", name = "Foreign Buyer LLC" };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> InvoiceAsync(Setup setup, SeriesDto series, object buyer, object[] lines, string? operationType) =>
        PostAsync(setup.Owner, "/api/v1/documents", new
        {
            seriesId = series.Id,
            issueDate = Iso(TodayInLima()),
            currency = "USD",
            buyer,
            lines,
            operationTypeCode = operationType,
        });

    private static async Task<DocumentDto> ExportAsync(Setup setup, decimal quantity = 2m)
    {
        var response = await InvoiceAsync(setup, setup.Invoice, ForeignBuyer, Lines("40", quantity), "0200");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
    }

    private static Task<HttpResponseMessage> NoteAsync(Setup setup, DocumentDto referenced, string reason, object[] lines) =>
        PostAsync(setup.Owner, "/api/v1/notes", new
        {
            seriesId = setup.CreditOfInvoice.Id,
            referencedDocumentId = referenced.Id,
            issueDate = Iso(TodayInLima()),
            reasonCode = reason,
            reason = "Ajuste de la exportación",
            lines,
        });

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

    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    [Fact]
    public async Task An_export_invoice_is_calculated_without_igv_stated_signed_accepted_and_printed()
    {
        var setup = await NewTenantAsync("Export Invoice SAC");

        var invoice = await ExportAsync(setup);

        Assert.Equal(100m, invoice.Totals.TotalExport);
        Assert.Equal(0m, invoice.Totals.TotalIgv);
        Assert.Equal(100m, invoice.Totals.PayableAmount);
        var read = (await setup.Owner.GetFromJsonAsync<DocumentDto>($"/api/v1/documents/{invoice.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal("0200", read.OperationTypeCode);
        Assert.Equal("0101", (await (await InvoiceAsync(setup, setup.Invoice, new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" }, Lines("10"), null)).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!.OperationTypeCode);

        var electronic = await AcceptAsync(setup, invoice);
        var xml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml");
        Assert.True(new XmlDsigSigner().Verify(xml).Value.IsValid);
        var root = XDocument.Parse(xml).Root!;
        Assert.Equal("0200", root.Element(Cbc + "InvoiceTypeCode")!.Attribute("listID")!.Value);
        Assert.Equal("USD", root.Element(Cbc + "DocumentCurrencyCode")!.Value);
        var scheme = root.Element(Cac + "TaxTotal")!.Element(Cac + "TaxSubtotal")!.Element(Cac + "TaxCategory")!.Element(Cac + "TaxScheme")!;
        Assert.Equal("9995", scheme.Element(Cbc + "ID")!.Value);
        var buyerId = root.Element(Cac + "AccountingCustomerParty")!.Element(Cac + "Party")!.Element(Cac + "PartyIdentification")!.Element(Cbc + "ID")!;
        Assert.Equal("0", buyerId.Attribute("schemeID")!.Value);

        Assert.Contains("Op. exportaci", PdfContent(await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exports_that_break_the_rules_are_refused_before_numbering()
    {
        var setup = await NewTenantAsync("Export Rules SAC");
        object ruc = new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" };
        object[] mixed = [.. Lines("40"), .. Lines("10")];

        var cases = new (string Name, Task<HttpResponseMessage> Response)[]
        {
            ("a RUC buyer", InvoiceAsync(setup, setup.Invoice, ruc, Lines("40"), "0200")),
            ("taxed lines in an export", InvoiceAsync(setup, setup.Invoice, ForeignBuyer, Lines("10"), "0200")),
            ("mixed lines", InvoiceAsync(setup, setup.Invoice, ForeignBuyer, mixed, "0200")),
            ("export lines in a sale", InvoiceAsync(setup, setup.Invoice, ruc, Lines("40"), null)),
            ("export lines with the sale type", InvoiceAsync(setup, setup.Invoice, ruc, Lines("40"), "0101")),
            ("an export receipt to a RUC buyer", InvoiceAsync(setup, setup.Receipt, ruc, Lines("40"), "0200")),
            ("an export receipt to a DNI buyer", InvoiceAsync(setup, setup.Receipt, new { documentTypeCode = "1", documentNumber = "12345678", name = "Persona Natural" }, Lines("40"), "0200")),
            ("taxed lines in an export receipt", InvoiceAsync(setup, setup.Receipt, ForeignBuyer, Lines("10"), "0200")),
            ("a lodging export receipt", InvoiceAsync(setup, setup.Receipt, ForeignBuyer, Lines("40"), "0202")),
            ("a services export without its country", InvoiceAsync(setup, setup.Invoice, ForeignBuyer, Lines("40"), "0201")),
            ("lodging and tourist package exports", InvoiceAsync(setup, setup.Invoice, ForeignBuyer, Lines("40"), "0202")),
            ("a tourist package export", InvoiceAsync(setup, setup.Invoice, ForeignBuyer, Lines("40"), "0205")),
            ("a foreign buyer in a sale", InvoiceAsync(setup, setup.Invoice, ForeignBuyer, Lines("10"), null)),
        };
        foreach (var (name, pending) in cases)
        {
            var response = await pending;
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{name}: {response.StatusCode}");
            Assert.Equal("SF-BIL-006", ProblemCode(await response.Content.ReadAsStringAsync()));
        }

        var first = await ExportAsync(setup);
        Assert.Equal(1, first.Number); // nothing refused took a number
    }

    private static Task<HttpResponseMessage> ServicesAsync(Setup setup, string operation, object buyer, string? country = null, string? series = null, object? detraction = null) =>
        PostAsync(setup.Owner, "/api/v1/documents", new
        {
            seriesId = (series == "03" ? setup.Receipt : setup.Invoice).Id,
            issueDate = Iso(TodayInLima()),
            currency = "USD",
            buyer,
            lines = Lines("40"),
            operationTypeCode = operation,
            usageCountryCode = country,
            detraction,
        });

    [Fact]
    public async Task An_export_of_services_is_issued_with_its_country_signed_accepted_and_printed()
    {
        var setup = await NewTenantAsync("Export Services SAC");

        var response = await ServicesAsync(setup, "0201", ForeignBuyer, "US");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var invoice = (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        var read = (await setup.Owner.GetFromJsonAsync<DocumentDto>($"/api/v1/documents/{invoice.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal("0201", read.OperationTypeCode);
        Assert.Equal("US", read.UsageCountryCode);
        Assert.Equal(100m, read.Totals.TotalExport);

        var electronic = await AcceptAsync(setup, invoice);
        var xml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml");
        Assert.True(new XmlDsigSigner().Verify(xml).Value.IsValid);
        var root = XDocument.Parse(xml).Root!;
        Assert.Equal("0201", root.Element(Cbc + "InvoiceTypeCode")!.Attribute("listID")!.Value);
        Assert.Equal("US", root.Element(Cac + "Delivery")!.Element(Cac + "DeliveryLocation")!.Element(Cac + "Address")!.Element(Cac + "Country")!.Element(Cbc + "IdentificationCode")!.Value);
        Assert.Contains("Op. exportaci", PdfContent(await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_buyer_of_an_export_of_services_depends_on_its_type()
    {
        var setup = await NewTenantAsync("Export Services Buyers SAC");
        object ruc = new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" };

        // 0200, 0201 and 0204 go abroad (rule 2800: no RUC); the others also take a buyer with RUC (verified in the beta).
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ServicesAsync(setup, "0201", ruc, "US")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ServicesAsync(setup, "0204", ruc)).StatusCode);
        foreach (var operation in new[] { "0203", "0206", "0207" })
        {
            Assert.Equal(HttpStatusCode.Created, (await ServicesAsync(setup, operation, ruc)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await ServicesAsync(setup, operation, ForeignBuyer)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Created, (await ServicesAsync(setup, "0204", ForeignBuyer)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ServicesAsync(setup, "0208", ruc, "CL")).StatusCode);
    }

    [Fact]
    public async Task The_country_of_use_is_required_by_0201_and_0208_and_refused_elsewhere()
    {
        var setup = await NewTenantAsync("Export Services Country SAC");

        var cases = new (string Name, Task<HttpResponseMessage> Response)[]
        {
            ("0201 without a country", ServicesAsync(setup, "0201", ForeignBuyer)),
            ("0208 without a country", ServicesAsync(setup, "0208", ForeignBuyer)),
            ("Peru as the country", ServicesAsync(setup, "0201", ForeignBuyer, "PE")),
            ("a lowercase country", ServicesAsync(setup, "0208", ForeignBuyer, "us")),
            ("a three-letter country", ServicesAsync(setup, "0208", ForeignBuyer, "USA")),
            ("a country in 0203", ServicesAsync(setup, "0203", ForeignBuyer, "US")),
            ("a country in an export of goods", ServicesAsync(setup, "0200", ForeignBuyer, "US")),
            ("a country in a sale", ServicesAsync(setup, "0101", ForeignBuyer, "US")),
            ("a services export on a receipt to a RUC buyer", ServicesAsync(setup, "0207", new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" }, series: "03")),
            ("a services export on a receipt without its country", ServicesAsync(setup, "0201", ForeignBuyer, series: "03")),
            ("a detraction on a services export", ServicesAsync(setup, "0203", ForeignBuyer, detraction: new { goodsOrServiceCode = "037", percentage = 12m, amount = 12m, accountNumber = "00012345678" })),
        };
        foreach (var (name, pending) in cases)
        {
            var response = await pending;
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{name}: {response.StatusCode}");
            Assert.Equal("SF-BIL-006", ProblemCode(await response.Content.ReadAsStringAsync()));
        }

        Assert.Equal(1, (await (await ServicesAsync(setup, "0201", ForeignBuyer, "US")).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!.Number); // nothing refused took a number
    }

    [Fact]
    public async Task An_export_receipt_is_issued_to_a_buyer_abroad_stated_accepted_printed_and_summarised()
    {
        var setup = await NewTenantAsync("Export Receipt SAC");

        var response = await ServicesAsync(setup, "0208", ForeignBuyer, "US", series: "03");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        var read = (await setup.Owner.GetFromJsonAsync<DocumentDto>($"/api/v1/documents/{receipt.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal("03", read.DocumentTypeCode);
        Assert.Equal("0208", read.OperationTypeCode);
        Assert.Equal(100m, read.Totals.TotalExport);
        Assert.Equal(100m, read.Totals.PayableAmount);
        var goods = await PostAsync(setup.Owner, "/api/v1/documents", new { seriesId = setup.Receipt.Id, issueDate = Iso(TodayInLima()), currency = "USD", buyer = ForeignBuyer, lines = Lines("40"), operationTypeCode = "0200" });
        Assert.Equal(HttpStatusCode.Created, goods.StatusCode);

        var electronic = (await (await setup.Owner.PostAsync($"/api/v1/documents/{receipt.Id}/electronic", null)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        var xml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml");
        Assert.True(new XmlDsigSigner().Verify(xml).Value.IsValid);
        var root = XDocument.Parse(xml).Root!;
        Assert.Equal("03", root.Element(Cbc + "InvoiceTypeCode")!.Value);
        Assert.Equal("0208", root.Element(Cbc + "InvoiceTypeCode")!.Attribute("listID")!.Value);
        Assert.Equal("US", root.Element(Cac + "Delivery")!.Element(Cac + "DeliveryLocation")!.Element(Cac + "Address")!.Element(Cac + "Country")!.Element(Cbc + "IdentificationCode")!.Value);
        Assert.Contains("Op. exportaci", PdfContent(await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf")), StringComparison.Ordinal);

        // Receipts reach SUNAT in the daily summary, with the export value under code 04.
        var created = await setup.Owner.PostAsJsonAsync("/api/v1/summaries", new { companyId = setup.Company.Id, referenceDate = Iso(TodayInLima()) });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var summary = Assert.Single((await created.Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);
        var summaryXml = XDocument.Parse(await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{summary.Document.Id}/xml"));
        XNamespace sac = "urn:sunat:names:specification:ubl:peru:schema:xsd:SunatAggregateComponents-1";
        var lines = summaryXml.Descendants(sac + "SummaryDocumentsLine").ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal("04", Assert.Single(l.Elements(sac + "BillingPayment")).Element(Cbc + "InstructionID")!.Value));
        Assert.All(lines, l => Assert.Equal("USD", l.Element(sac + "TotalAmount")!.Attribute("currencyID")!.Value));
    }

    [Fact]
    public async Task A_credit_note_of_reason_11_adjusts_an_export_and_is_accepted()
    {
        var setup = await NewTenantAsync("Export Adjustment SAC");
        var invoice = await ExportAsync(setup);

        var response = await NoteAsync(setup, invoice, "11", Lines("40", 1m));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var note = (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(50m, note.Totals.TotalExport);
        Assert.Equal("11", note.Note!.ReasonCode);

        await AcceptAsync(setup, invoice);
        var electronic = await AcceptAsync(setup, note);
        var xml = XDocument.Parse(await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml")).Root!;
        Assert.Equal("11", xml.Element(Cac + "DiscrepancyResponse")!.Element(Cbc + "ResponseCode")!.Value);
        Assert.Equal("9995", xml.Element(Cac + "TaxTotal")!.Element(Cac + "TaxSubtotal")!.Element(Cac + "TaxCategory")!.Element(Cac + "TaxScheme")!.Element(Cbc + "ID")!.Value);
    }

    [Fact]
    public async Task Export_notes_that_break_the_rules_are_refused_and_the_credit_is_accumulated()
    {
        var setup = await NewTenantAsync("Export Notes Rules SAC");
        var export = await ExportAsync(setup); // 100.00
        var domestic = (await (await InvoiceAsync(setup, setup.Invoice, new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" }, Lines("10"), null)).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        object[] mixed = [.. Lines("40", 1m), .. Lines("10", 1m)];

        var cases = new (string Name, Task<HttpResponseMessage> Response)[]
        {
            ("reason 11 on a domestic invoice", NoteAsync(setup, domestic, "11", Lines("40", 1m))),
            ("reason 11 with taxed lines", NoteAsync(setup, export, "11", Lines("10", 1m))),
            ("export lines mixed with others", NoteAsync(setup, export, "01", mixed)),
        };
        foreach (var (name, pending) in cases)
        {
            var response = await pending;
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{name}: {response.StatusCode}");
            Assert.Equal("SF-BIL-006", ProblemCode(await response.Content.ReadAsStringAsync()));
        }

        // Another reason may annul the export with export lines; the credit accumulates against the export total.
        Assert.Equal(HttpStatusCode.Created, (await NoteAsync(setup, export, "11", Lines("40", 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await NoteAsync(setup, export, "01", Lines("40", 1m))).StatusCode);
        var third = await NoteAsync(setup, export, "11", Lines("40", 1m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, third.StatusCode);
        Assert.Equal("SF-BIL-010", ProblemCode(await third.Content.ReadAsStringAsync()));
    }
}
