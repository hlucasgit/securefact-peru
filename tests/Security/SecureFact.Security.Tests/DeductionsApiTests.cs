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

/// <summary>Detraction (SPOT) and IGV withholding of an invoice, alone and with a credit sale, end to end.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class DeductionsApiTests(ApiFixture api)
{
    private static int _rucCounter = 26_000_000;

    private sealed record Setup(Guid TenantId, HttpClient Owner, CompanyDto Company, SeriesDto Invoice, SeriesDto Receipt);

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

    private static string Day(int days) => Iso(TodayInLima().AddDays(days));

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
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = "Sol-Clave-ded-1" })).StatusCode);

        async Task<SeriesDto> SeriesAsync(string type, string code) =>
            (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = type, code })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        return new Setup(tenantId, owner, company, await SeriesAsync("01", "F001"), await SeriesAsync("03", "B001"));
    }

    private static readonly object[] Lines =
        [new { description = "Servicio de consultoría", unitCode = "ZZ", tax = new { quantity = 2m, unitValue = 100m, igvAffectationCode = "10" } }]; // 236.00

    private static object Detraction(decimal amount = 28m, decimal percentage = 12m, string code = "037", string account = "00012345678") =>
        new { goodsOrServiceCode = code, percentage, amount, accountNumber = account };

    private static object Cuota(decimal amount, string dueDate) => new { amount, dueDate };

    private static object Body(Setup setup, string currency = "PEN", object? detraction = null, object? retention = null, object[]? installments = null, string? operationType = null, bool receipt = false) => new
    {
        seriesId = receipt ? setup.Receipt.Id : setup.Invoice.Id,
        issueDate = Iso(TodayInLima()),
        currency,
        buyer = receipt
            ? new { documentTypeCode = "1", documentNumber = "12345678", name = "Persona Natural" }
            : new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
        lines = Lines,
        detraction,
        retention,
        installments,
        operationTypeCode = operationType,
    };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
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

    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    [Fact]
    public async Task An_invoice_subject_to_detraction_is_stated_signed_accepted_and_printed()
    {
        var setup = await NewTenantAsync("Detraction SAC");

        var response = await PostAsync(setup.Owner, Body(setup, detraction: Detraction()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var invoice = (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(236m, invoice.Totals.PayableAmount);
        var read = (await setup.Owner.GetFromJsonAsync<DocumentDto>($"/api/v1/documents/{invoice.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal("1001", read.OperationTypeCode);
        Assert.Equal(28m, read.Detraction!.Amount);
        Assert.Equal("037", read.Detraction.GoodsOrServiceCode);

        var electronic = await AcceptAsync(setup, invoice);
        var xml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml");
        Assert.True(new XmlDsigSigner().Verify(xml).Value.IsValid);
        var root = XDocument.Parse(xml).Root!;
        Assert.Equal("1001", root.Element(Cbc + "InvoiceTypeCode")!.Attribute("listID")!.Value);
        Assert.Equal("00012345678", root.Element(Cac + "PaymentMeans")!.Element(Cac + "PayeeFinancialAccount")!.Element(Cbc + "ID")!.Value);
        Assert.Contains(root.Elements(Cac + "PaymentTerms"), t => t.Element(Cbc + "ID")!.Value == "Detraccion" && t.Element(Cbc + "Amount")!.Value == "28.00");
        Assert.Equal("2006", root.Element(Cbc + "Note")!.Attribute("languageLocaleID")!.Value);

        Assert.Contains("Operación sujeta a detracción", PdfContent(await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invoice_with_an_igv_withholding_states_the_allowance_62_and_prints_it()
    {
        var setup = await NewTenantAsync("Retention SAC");

        var response = await PostAsync(setup.Owner, Body(setup, retention: new { percentage = 3m }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var invoice = (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        var read = (await setup.Owner.GetFromJsonAsync<DocumentDto>($"/api/v1/documents/{invoice.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal("0101", read.OperationTypeCode);
        Assert.Equal((3m, 236m, 7.08m), (read.Retention!.Percentage, read.Retention.BaseAmount, read.Retention.Amount));

        var electronic = await AcceptAsync(setup, invoice);
        var root = XDocument.Parse(await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml")).Root!;
        var allowance = root.Element(Cac + "AllowanceCharge")!;
        Assert.Equal("62", allowance.Element(Cbc + "AllowanceChargeReasonCode")!.Value);
        Assert.Equal("7.08", allowance.Element(Cbc + "Amount")!.Value);
        Assert.Equal("236.00", root.Element(Cac + "LegalMonetaryTotal")!.Element(Cbc + "PayableAmount")!.Value);
        Assert.Contains("Retención del IGV", PdfContent(await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_credit_sale_with_a_detraction_or_a_withholding_has_installments_that_leave_it_out()
    {
        var setup = await NewTenantAsync("Deductions Credit SAC");

        var detracted = await PostAsync(setup.Owner, Body(setup, detraction: Detraction(), installments: [Cuota(100m, Day(30)), Cuota(108m, Day(60))])); // 236 - 28
        var retained = await PostAsync(setup.Owner, Body(setup, retention: new { percentage = 3m }, installments: [Cuota(228.92m, Day(30))])); // 236 - 7.08
        Assert.Equal(HttpStatusCode.Created, detracted.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retained.StatusCode);

        var ignoring = await PostAsync(setup.Owner, Body(setup, detraction: Detraction(), installments: [Cuota(236m, Day(30))]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ignoring.StatusCode);
        Assert.Equal("SF-BIL-006", ProblemCode(await ignoring.Content.ReadAsStringAsync()));

        // The net pending amount of the accepted credit invoice with detraction is stated without it.
        var invoice = (await detracted.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        var electronic = await AcceptAsync(setup, invoice);
        var terms = XDocument.Parse(await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml")).Root!.Elements(Cac + "PaymentTerms");
        var credit = Assert.Single(terms, t => t.Element(Cbc + "PaymentMeansID")!.Value == "Credito");
        Assert.Equal("208.00", credit.Element(Cbc + "Amount")!.Value);
    }

    private static Task<HttpResponseMessage> SetDetractionAccountAsync(Setup setup, string? account) =>
        setup.Owner.PutAsJsonAsync($"/api/v1/companies/{setup.Company.Id}", new
        {
            legalName = "Emisora SAC",
            fiscalAddress = "Av. Larco 123",
            ubigeo = "150122",
            detractionAccount = account,
        });

    [Fact]
    public async Task A_detraction_without_an_account_uses_the_one_registered_in_the_company_and_the_issued_document_keeps_it()
    {
        var setup = await NewTenantAsync("Detraction Account SAC");
        object noAccount = new { goodsOrServiceCode = "037", percentage = 12m, amount = 28m };

        // No account in the request or in the company: refused, and nothing is numbered.
        var refused = await PostAsync(setup.Owner, Body(setup, detraction: noAccount));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("SF-BIL-006", ProblemCode(await refused.Content.ReadAsStringAsync()));

        var updated = await SetDetractionAccountAsync(setup, "00098765432");
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("00098765432", (await updated.Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!.DetractionAccount);
        var first = (await (await PostAsync(setup.Owner, Body(setup, detraction: noAccount))).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(1, first.Number);
        Assert.Equal("00098765432", first.Detraction!.AccountNumber);

        // The request may name another account, and a later change of the company leaves the issued document alone.
        var named = (await (await PostAsync(setup.Owner, Body(setup, detraction: Detraction(account: "11111111111")))).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal("11111111111", named.Detraction!.AccountNumber);
        Assert.Equal(HttpStatusCode.OK, (await SetDetractionAccountAsync(setup, "22222222222")).StatusCode);
        var read = (await setup.Owner.GetFromJsonAsync<DocumentDto>($"/api/v1/documents/{first.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal("00098765432", read.Detraction!.AccountNumber);

        var electronic = (await (await setup.Owner.PostAsync($"/api/v1/documents/{first.Id}/electronic", null)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        var xml = XDocument.Parse(await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml")).Root!;
        Assert.Equal("00098765432", xml.Element(Cac + "PaymentMeans")!.Element(Cac + "PayeeFinancialAccount")!.Element(Cbc + "ID")!.Value);
    }

    [Fact]
    public async Task The_detraction_account_of_a_company_is_validated_and_can_be_cleared()
    {
        var setup = await NewTenantAsync("Detraction Account Rules SAC");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetDetractionAccountAsync(setup, "00 12!")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetDetractionAccountAsync(setup, new string('1', 101))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetDetractionAccountAsync(setup, "  00012345678  ")).StatusCode);
        Assert.Equal("00012345678", (await setup.Owner.GetFromJsonAsync<CompanyDto>($"/api/v1/companies/{setup.Company.Id}", ApiFixture.JsonOptions))!.DetractionAccount);
        var cleared = await SetDetractionAccountAsync(setup, null);
        Assert.Null((await cleared.Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!.DetractionAccount);
    }

    [Fact]
    public async Task Detractions_and_withholdings_that_break_the_rules_are_refused_before_numbering()
    {
        var setup = await NewTenantAsync("Deductions Rules SAC");
        object retention = new { percentage = 3m };

        var cases = new (string Name, object Body)[]
        {
            ("a detraction in dollars", Body(setup, "USD", Detraction())),
            ("an unknown goods code", Body(setup, detraction: Detraction(code: "999"))),
            ("a transport code", Body(setup, detraction: Detraction(code: "027"))),
            ("percentage zero", Body(setup, detraction: Detraction(percentage: 0m))),
            ("percentage above 100", Body(setup, detraction: Detraction(amount: 236m, percentage: 101m))),
            ("an amount that does not match", Body(setup, detraction: Detraction(amount: 50m))),
            ("an amount above the total", Body(setup, detraction: Detraction(amount: 300m, percentage: 127m))),
            ("an amount with three decimals", Body(setup, detraction: Detraction(amount: 28.001m))),
            ("an invalid account", Body(setup, detraction: Detraction(account: "00 12!"))),
            ("no account", Body(setup, detraction: Detraction(account: ""))),
            ("a detraction and a withholding", Body(setup, detraction: Detraction(), retention: retention)),
            ("operation 1001 without a detraction", Body(setup, operationType: "1001")),
            ("a detraction on an export", Body(setup, detraction: Detraction(), operationType: "0200")),
            ("a detraction on a receipt", Body(setup, detraction: Detraction(), receipt: true)),
            ("a withholding on a receipt", Body(setup, retention: retention, receipt: true)),
            ("a withholding of zero", Body(setup, retention: new { percentage = 0m })),
            ("a withholding of 100", Body(setup, retention: new { percentage = 100m })),
        };
        foreach (var (name, body) in cases)
        {
            var response = await PostAsync(setup.Owner, body);
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{name}: {response.StatusCode}");
            Assert.Equal("SF-BIL-006", ProblemCode(await response.Content.ReadAsStringAsync()));
        }

        var first = (await (await PostAsync(setup.Owner, Body(setup, detraction: Detraction()))).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(1, first.Number); // nothing refused took a number
    }
}
