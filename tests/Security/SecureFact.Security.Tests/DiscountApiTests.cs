using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml.XPath;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>Line and global discounts and charges, end to end: Billing keeps what was requested, the electronic document states it, and the daily summary and the PDF follow.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class DiscountApiTests(ApiFixture api)
{
    private static int _rucCounter = 22_000_000;

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
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = "Sol-Clave-disc-1" })).StatusCode);

        async Task<SeriesDto> SeriesAsync(string type, string code) =>
            (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = type, code })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        return new Setup(tenantId, owner, company, await SeriesAsync("01", "F001"), await SeriesAsync("03", "B001"), await SeriesAsync("07", "FC01"));
    }

    private static readonly object[] AdjustedLines =
    [
        new
        {
            description = "Servicio con ajustes",
            unitCode = "ZZ",
            tax = new { quantity = 2m, unitValue = 100m, igvAffectationCode = "10", discountAffectingBase = 20m, chargeAffectingBase = 5m, discountNotAffectingBase = 10m, chargeNotAffectingBase = 3m },
        },
        new { description = "Servicio exonerado", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 50m, igvAffectationCode = "20" } },
    ];

    private static readonly object GlobalAdjustment = new { discountAffectingBase = 12m, chargeAffectingBase = 4m, discountNotAffectingBase = 7m, chargeNotAffectingBase = 2m };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    private static object InvoiceBody(SeriesDto series, bool receipt, object lines, object? adjustments) => new
    {
        seriesId = series.Id,
        issueDate = Iso(TodayInLima()),
        currency = "PEN",
        buyer = receipt
            ? new { documentTypeCode = "1", documentNumber = "12345678", name = "Persona Natural" }
            : new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
        lines,
        adjustments,
    };

    private static async Task<DocumentDto> IssueAsync(HttpClient client, SeriesDto series, bool receipt, object lines, object? adjustments)
    {
        var response = await PostAsync(client, "/api/v1/documents", InvoiceBody(series, receipt, lines, adjustments));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
    }

    private static async Task<ElectronicDocumentDto> PrepareAsync(HttpClient client, Guid documentId)
    {
        var response = await client.PostAsync($"/api/v1/documents/{documentId}/electronic", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
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
    public async Task An_invoice_with_line_and_global_discounts_is_calculated_stated_signed_and_accepted()
    {
        var setup = await NewTenantAsync("Discount Invoice SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false, AdjustedLines, GlobalAdjustment);

        // Billing: taxed 185 - 12 + 4 = 177, exempt 50, IGV 18 % of 177, charges and discounts that leave the base alone in the payable amount.
        Assert.Equal(177m, invoice.Totals.TotalTaxableGravado);
        Assert.Equal(31.86m, invoice.Totals.TotalIgv);
        Assert.Equal(10m + 7m, invoice.Totals.TotalAllowances);
        Assert.Equal(3m + 2m, invoice.Totals.TotalCharges);
        Assert.Equal(177m + 50m + 31.86m + 5m - 17m, invoice.Totals.PayableAmount);

        // What was asked for survives the reading of the document.
        var read = (await setup.Owner.GetFromJsonAsync<DocumentDto>($"/api/v1/documents/{invoice.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal(20m, read.Lines[0].DiscountAffectingBase);
        Assert.Equal(5m, read.Lines[0].ChargeAffectingBase);
        Assert.Equal(10m, read.Lines[0].DiscountNotAffectingBase);
        Assert.Equal(3m, read.Lines[0].ChargeNotAffectingBase);
        Assert.Equal(12m, read.Adjustments!.DiscountAffectingBase);
        Assert.Equal(2m, read.Adjustments.ChargeNotAffectingBase);

        var electronic = await PrepareAsync(setup.Owner, invoice.Id);
        var xml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml");
        Assert.True(new XmlDsigSigner().Verify(xml).Value.IsValid);
        var document = XDocument.Parse(xml);
        XNamespace cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
        XNamespace cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        var globalCodes = document.Root!.Elements(cac + "AllowanceCharge").Select(e => e.Element(cbc + "AllowanceChargeReasonCode")!.Value).ToArray();
        var lineCodes = document.Root.Elements(cac + "InvoiceLine").First().Elements(cac + "AllowanceCharge").Select(e => e.Element(cbc + "AllowanceChargeReasonCode")!.Value).ToArray();
        Assert.Equal(["02", "49", "03", "50"], globalCodes);
        Assert.Equal(["00", "47", "01", "48"], lineCodes);
        var monetary = document.Root.Element(cac + "LegalMonetaryTotal")!;
        Assert.Equal("17.00", monetary.Element(cbc + "AllowanceTotalAmount")!.Value);
        Assert.Equal("5.00", monetary.Element(cbc + "ChargeTotalAmount")!.Value);
        Assert.Equal("246.86", monetary.Element(cbc + "PayableAmount")!.Value);

        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, $"{invoice.Series}-{invoice.Number}")));
        var sent = (await (await setup.Owner.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/send", null)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(EDocumentState.Accepted, sent.State);

        var pdf = PdfContent(await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf"));
        Assert.Contains("(Otros cargos)", pdf, StringComparison.Ordinal);
        Assert.Contains("(Otros descuentos)", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_receipt_with_charges_and_discounts_goes_into_the_daily_summary_with_its_other_charges()
    {
        var setup = await NewTenantAsync("Discount Receipt SAC");
        object[] lines =
        [
            new { description = "Producto", unitCode = "NIU", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10", discountAffectingBase = 10m, chargeNotAffectingBase = 5m, discountNotAffectingBase = 3m } },
        ];
        var receipt = await IssueAsync(setup.Owner, setup.Receipt, receipt: true, lines, null);
        Assert.Equal(90m + 16.2m + 5m - 3m, receipt.Totals.PayableAmount);

        var created = await setup.Owner.PostAsJsonAsync("/api/v1/summaries", new { companyId = setup.Company.Id, referenceDate = Iso(TodayInLima()) });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var summary = Assert.Single((await created.Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);
        var xml = XDocument.Parse(await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{summary.Document.Id}/xml"));
        XNamespace cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
        XNamespace cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        XNamespace sac = "urn:sunat:names:specification:ubl:peru:schema:xsd:SunatAggregateComponents-1";
        var line = xml.Descendants(sac + "SummaryDocumentsLine").Single();
        Assert.Equal("5.00", line.Element(cac + "AllowanceCharge")!.Element(cbc + "Amount")!.Value);
        Assert.Equal("108.20", line.Element(sac + "TotalAmount")!.Value);
        Assert.Equal("90.00", line.Element(sac + "BillingPayment")!.Element(cbc + "PaidAmount")!.Value);
    }

    [Fact]
    public async Task A_note_cannot_carry_discounts_or_charges_and_invalid_adjustments_are_refused()
    {
        var setup = await NewTenantAsync("Discount Note SAC");
        object[] plain = [new { description = "Servicio", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10" } }];
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false, plain, null);

        object NoteBody(object lines, object? adjustments) => new
        {
            seriesId = setup.CreditOfInvoice.Id,
            referencedDocumentId = invoice.Id,
            issueDate = Iso(TodayInLima()),
            reasonCode = "01",
            reason = "Anulación de la operación",
            lines,
            adjustments,
        };

        object[] discountedLine = [new { description = "Servicio", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10", discountAffectingBase = 5m } }];
        var withLineDiscount = await PostAsync(setup.Owner, "/api/v1/notes", NoteBody(discountedLine, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withLineDiscount.StatusCode);
        Assert.Equal("SF-BIL-006", ProblemCode(await withLineDiscount.Content.ReadAsStringAsync()));
        var withGlobal = await PostAsync(setup.Owner, "/api/v1/notes", NoteBody(plain, new { discountAffectingBase = 5m }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withGlobal.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(setup.Owner, "/api/v1/notes", NoteBody(plain, null))).StatusCode);

        // A discount over the whole line, or over the whole taxed value, is not a document.
        object[] tooMuch = [new { description = "Servicio", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10", discountAffectingBase = 150m } }];
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(setup.Owner, "/api/v1/documents", InvoiceBody(setup.Invoice, false, tooMuch, null))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(setup.Owner, "/api/v1/documents", InvoiceBody(setup.Invoice, false, plain, new { discountAffectingBase = 150m }))).StatusCode);
    }

    [Fact]
    public async Task A_charge_over_a_line_of_zero_value_is_kept_in_billing_but_the_electronic_document_is_refused_explicitly()
    {
        var setup = await NewTenantAsync("Discount Edge SAC");
        object[] lines = [new { description = "Servicio", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10", discountAffectingBase = 100m, chargeNotAffectingBase = 5m } }];
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false, lines, null);

        var response = await setup.Owner.PostAsync($"/api/v1/documents/{invoice.Id}/electronic", null);

        Assert.True(response.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest, response.StatusCode.ToString());
        Assert.Equal("SF-CPE-002", ProblemCode(await response.Content.ReadAsStringAsync()));
    }
}
