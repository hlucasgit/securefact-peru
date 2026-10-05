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

/// <summary>The legends of the exonerated sales (catalogue 52: 2001, 2002, 2003 and 2008) in invoices and receipts, end to end.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class LegendApiTests(ApiFixture api)
{
    private static int _rucCounter = 28_000_000;

    private sealed record Setup(HttpClient Owner, CompanyDto Company, SeriesDto Invoice, SeriesDto Receipt);

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
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = "Sol-Clave-leg-1" })).StatusCode);

        async Task<SeriesDto> SeriesAsync(string type, string code) =>
            (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = type, code })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        return new Setup(owner, company, await SeriesAsync("01", "F001"), await SeriesAsync("03", "B001"));
    }

    private static object[] Line(string affectation) =>
        [new { description = "Producto", unitCode = "NIU", tax = new { quantity = 2m, unitValue = 50m, igvAffectationCode = affectation } }];

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SaleAsync(Setup setup, bool receipt, string affectation, string?[]? legends, string currency = "PEN", string? operationType = null) =>
        PostAsync(setup.Owner, new
        {
            seriesId = receipt ? setup.Receipt.Id : setup.Invoice.Id,
            issueDate = Iso(TodayInLima()),
            currency,
            buyer = operationType == "0200"
                ? new { documentTypeCode = "0", documentNumber = "-", name = "Foreign Buyer LLC" }
                : receipt
                    ? new { documentTypeCode = "1", documentNumber = "12345678", name = "Persona Natural" }
                    : new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
            lines = Line(affectation),
            operationTypeCode = operationType,
            legendCodes = legends,
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

    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    [Fact]
    public async Task An_invoice_of_exonerated_operations_states_the_legend_2008_signed_accepted_and_printed()
    {
        var setup = await NewTenantAsync("Tacna Legend SAC");

        var response = await SaleAsync(setup, receipt: false, "20", ["2008"]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var invoice = (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        var read = (await setup.Owner.GetFromJsonAsync<DocumentDto>($"/api/v1/documents/{invoice.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal(["2008"], read.LegendCodes);
        Assert.Equal(100m, read.Totals.TotalExempt);

        var electronic = await AcceptAsync(setup, invoice);
        var xml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml");
        Assert.True(new XmlDsigSigner().Verify(xml).Value.IsValid);
        var note = Assert.Single(XDocument.Parse(xml).Root!.Elements(Cbc + "Note"));
        Assert.Equal("2008", note.Attribute("languageLocaleID")!.Value);
        Assert.Equal("VENTA EXONERADA DEL IGV-ISC-IPM. PROHIBIDA LA VENTA FUERA DE LA ZONA COMERCIAL DE TACNA", note.Value);

        Assert.Contains("ZONA COMERCIAL DE TACNA", PdfContent(await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_receipt_states_the_amazon_legends_and_several_legends_go_together()
    {
        var setup = await NewTenantAsync("Amazon Legend SAC");

        var receipt = await SaleAsync(setup, receipt: true, "20", ["2001"]);
        var several = await SaleAsync(setup, receipt: false, "20", ["2001", "2002", "2003"]);

        Assert.Equal(HttpStatusCode.Created, receipt.StatusCode);
        Assert.Equal(HttpStatusCode.Created, several.StatusCode);
        var document = (await several.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        var electronic = await AcceptAsync(setup, document);
        var notes = XDocument.Parse(await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml")).Root!.Elements(Cbc + "Note").ToList();
        Assert.Equal(["2001", "2002", "2003"], notes.Select(n => n.Attribute("languageLocaleID")!.Value));
        Assert.Contains("CONTRATOS DE CONSTRUCCIÓN EJECUTADOS EN LA AMAZONÍA REGIÓN SELVA", notes[2].Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legends_that_break_the_rules_are_refused_before_numbering()
    {
        var setup = await NewTenantAsync("Legend Rules SAC");

        var cases = new (string Name, Task<HttpResponseMessage> Response)[]
        {
            ("a legend on a taxed invoice", SaleAsync(setup, false, "10", ["2008"])),
            ("a legend on a taxed receipt", SaleAsync(setup, true, "10", ["2001"])),
            ("a legend on an unaffected invoice", SaleAsync(setup, false, "30", ["2008"])),
            ("an unknown legend", SaleAsync(setup, false, "20", ["2009"])),
            ("a legend of another purpose", SaleAsync(setup, false, "20", ["2007"])),
            ("a repeated legend", SaleAsync(setup, false, "20", ["2008", "2008"])),
            ("an empty legend", SaleAsync(setup, false, "20", [""])),
            ("a null legend", SaleAsync(setup, false, "20", [null])),
            ("a legend on an export", SaleAsync(setup, false, "40", ["2008"], "USD", "0200")),
        };
        foreach (var (name, pending) in cases)
        {
            var response = await pending;
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{name}: {response.StatusCode}");
            Assert.Equal("SF-BIL-006", ProblemCode(await response.Content.ReadAsStringAsync()));
        }

        var first = (await (await SaleAsync(setup, false, "20", ["2008"])).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(1, first.Number); // nothing refused took a number
    }
}
