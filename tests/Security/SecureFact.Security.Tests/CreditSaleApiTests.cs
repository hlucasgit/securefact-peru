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

/// <summary>Sales on credit with installments, end to end: Billing validates the plan, the electronic document states it, the PDF prints it.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class CreditSaleApiTests(ApiFixture api)
{
    private static int _rucCounter = 23_000_000;

    private sealed record Setup(Guid TenantId, HttpClient Owner, CompanyDto Company, SeriesDto Invoice, SeriesDto Receipt, SeriesDto CreditOfInvoice, SeriesDto DebitOfInvoice);

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
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = "Sol-Clave-cred-1" })).StatusCode);

        async Task<SeriesDto> SeriesAsync(string type, string code) =>
            (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = type, code })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        return new Setup(tenantId, owner, company, await SeriesAsync("01", "F001"), await SeriesAsync("03", "B001"), await SeriesAsync("07", "FC01"), await SeriesAsync("08", "FD01"));
    }

    private static readonly object[] OneLine = [new { description = "Servicio", unitCode = "ZZ", tax = new { quantity = 2m, unitValue = 100m, igvAffectationCode = "10" } }];

    private static object Cuota(decimal amount, string dueDate) => new { amount, dueDate };

    private static object Body(SeriesDto series, bool receipt, object[]? installments) => new
    {
        seriesId = series.Id,
        issueDate = Iso(TodayInLima()),
        currency = "PEN",
        buyer = receipt
            ? new { documentTypeCode = "1", documentNumber = "12345678", name = "Persona Natural" }
            : new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
        lines = OneLine,
        installments,
    };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, object body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
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
    public async Task An_invoice_on_credit_keeps_its_installments_states_them_in_the_signed_xml_and_prints_them()
    {
        var setup = await NewTenantAsync("Credit Invoice SAC");

        var response = await PostAsync(setup.Owner, Body(setup.Invoice, false, [Cuota(100m, Day(30)), Cuota(136m, Day(60))]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var invoice = (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(236m, invoice.Totals.PayableAmount);
        var read = (await setup.Owner.GetFromJsonAsync<DocumentDto>($"/api/v1/documents/{invoice.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal(100m, read.Installments![0].Amount);
        Assert.Equal(136m, read.Installments[1].Amount);
        Assert.Equal(TodayInLima().AddDays(60), read.Installments[1].DueDate);

        var prepared = await setup.Owner.PostAsync($"/api/v1/documents/{invoice.Id}/electronic", null);
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var electronic = (await prepared.Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        var xml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml");
        Assert.True(new XmlDsigSigner().Verify(xml).Value.IsValid);
        XNamespace cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
        XNamespace cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        var terms = XDocument.Parse(xml).Root!.Elements(cac + "PaymentTerms").ToList();
        Assert.Equal(["Credito", "Cuota001", "Cuota002"], terms.Select(t => t.Element(cbc + "PaymentMeansID")!.Value).ToArray());
        Assert.Equal(["236.00", "100.00", "136.00"], terms.Select(t => t.Element(cbc + "Amount")!.Value).ToArray());
        Assert.Equal(Day(60), terms[2].Element(cbc + "PaymentDueDate")!.Value);

        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, $"{invoice.Series}-{invoice.Number}")));
        var sent = (await (await setup.Owner.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/send", null)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(EDocumentState.Accepted, sent.State);

        var pdf = PdfContent(await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf"));
        Assert.Contains("(Cuota 2)", pdf, StringComparison.Ordinal);
        Assert.Contains("(Monto neto pendiente de pago: S/ 236.00)", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Installments_that_break_the_rules_are_refused_before_numbering()
    {
        var setup = await NewTenantAsync("Credit Rules SAC");

        async Task<long> LastNumberAsync() =>
            (await setup.Owner.GetFromJsonAsync<List<SeriesDto>>($"/api/v1/series?companyId={setup.Company.Id}", ApiFixture.JsonOptions))!.Single(s => s.Id == setup.Invoice.Id).LastNumber;

        var before = await LastNumberAsync();
        var cases = new (string Name, object Body)[]
        {
            ("does not add up", Body(setup.Invoice, false, [Cuota(100m, Day(30)), Cuota(100m, Day(60))])),
            ("due on the issue date", Body(setup.Invoice, false, [Cuota(236m, Day(0))])),
            ("due in the past", Body(setup.Invoice, false, [Cuota(236m, Day(-3))])),
            ("zero amount", Body(setup.Invoice, false, [Cuota(0m, Day(30)), Cuota(236m, Day(40))])),
            ("three decimals", Body(setup.Invoice, false, [Cuota(235.999m, Day(30)), Cuota(0.001m, Day(40))])),
            ("no installments", Body(setup.Invoice, false, [])),
            ("a receipt", Body(setup.Receipt, true, [Cuota(236m, Day(30))])),
        };
        foreach (var (name, body) in cases)
        {
            var response = await PostAsync(setup.Owner, body);
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{name}: {response.StatusCode}");
            Assert.Equal("SF-BIL-006", ProblemCode(await response.Content.ReadAsStringAsync()));
        }

        Assert.Equal(before, await LastNumberAsync()); // nothing was numbered: no gap in the series
    }

    // ---------- credit note of reason 13: adjustment of the installments ----------

    private static object AdjustmentBody(SeriesDto series, DocumentDto invoice, object[]? installments, string reason = "13", object[]? lines = null) => new
    {
        seriesId = series.Id,
        referencedDocumentId = invoice.Id,
        issueDate = Iso(TodayInLima()),
        reasonCode = reason,
        reason = "Ampliación del plazo de pago",
        lines,
        installments,
    };

    private static async Task<HttpResponseMessage> PostNoteAsync(HttpClient client, object body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notes") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    private static async Task<DocumentDto> CreditInvoiceAsync(Setup setup)
    {
        var response = await PostAsync(setup.Owner, Body(setup.Invoice, false, [Cuota(100m, Day(30)), Cuota(136m, Day(60))]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
    }

    [Fact]
    public async Task A_credit_note_of_reason_13_replaces_the_installments_with_a_zero_value_note_and_is_accepted()
    {
        var setup = await NewTenantAsync("Credit Adjust SAC");
        var invoice = await CreditInvoiceAsync(setup);

        var response = await PostNoteAsync(setup.Owner, AdjustmentBody(setup.CreditOfInvoice, invoice, [Cuota(60m, Day(45)), Cuota(80m, Day(90))]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var note = (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(0m, note.Totals.PayableAmount);
        Assert.Equal("13", note.Note!.ReasonCode);
        var line = Assert.Single(note.Lines);
        Assert.Equal(0m, line.LineExtensionAmount);
        Assert.Equal([60m, 80m], note.Installments!.Select(i => i.Amount).ToArray());

        // The note waits for the invoice to be accepted, then goes by sendBill like any note of an invoice.
        var invoiceElectronic = (await (await setup.Owner.PostAsync($"/api/v1/documents/{invoice.Id}/electronic", null)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, $"{invoice.Series}-{invoice.Number}")));
        Assert.Equal(EDocumentState.Accepted, (await (await setup.Owner.PostAsync($"/api/v1/electronic-documents/{invoiceElectronic.Id}/send", null)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!.State);

        var prepared = await setup.Owner.PostAsync($"/api/v1/documents/{note.Id}/electronic", null);
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var electronic = (await prepared.Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        var xml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml");
        Assert.True(new XmlDsigSigner().Verify(xml).Value.IsValid);
        XNamespace cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
        XNamespace cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        var root = XDocument.Parse(xml).Root!;
        Assert.Equal(["Credito", "Cuota001", "Cuota002"], root.Elements(cac + "PaymentTerms").Select(t => t.Element(cbc + "PaymentMeansID")!.Value).ToArray());
        Assert.Equal("13", root.Element(cac + "DiscrepancyResponse")!.Element(cbc + "ResponseCode")!.Value);
        Assert.Equal("0.00", root.Element(cac + "LegalMonetaryTotal")!.Element(cbc + "PayableAmount")!.Value);

        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, $"{note.Series}-{note.Number}")));
        var sent = (await (await setup.Owner.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/send", null)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(EDocumentState.Accepted, sent.State);
        Assert.Contains("(Cuota 2)", PdfContent(await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_note_of_reason_13_that_breaks_the_rules_is_refused()
    {
        var setup = await NewTenantAsync("Credit Adjust Rules SAC");
        var credit = await CreditInvoiceAsync(setup);
        var cash = (await (await PostAsync(setup.Owner, Body(setup.Invoice, false, null))).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        var receipt = (await (await PostAsync(setup.Owner, Body(setup.Receipt, true, null))).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        object[] valid = [Cuota(236m, Day(90))];
        object[] oneLine = [new { description = "Servicio", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 10m, igvAffectationCode = "10" } }];

        var cases = new (string Name, object Body)[]
        {
            ("a cash invoice", AdjustmentBody(setup.CreditOfInvoice, cash, valid)),
            ("a receipt", AdjustmentBody(setup.CreditOfInvoice, receipt, valid)),
            ("no installments", AdjustmentBody(setup.CreditOfInvoice, credit, null)),
            ("empty installments", AdjustmentBody(setup.CreditOfInvoice, credit, [])),
            ("lines given", AdjustmentBody(setup.CreditOfInvoice, credit, valid, lines: oneLine)),
            ("more than the invoice", AdjustmentBody(setup.CreditOfInvoice, credit, [Cuota(236.01m, Day(90))])),
            ("due before the invoice", AdjustmentBody(setup.CreditOfInvoice, credit, [Cuota(236m, Day(-1))])),
            ("due on the invoice date", AdjustmentBody(setup.CreditOfInvoice, credit, [Cuota(236m, Day(0))])),
            ("zero amount", AdjustmentBody(setup.CreditOfInvoice, credit, [Cuota(0m, Day(30))])),
            ("a debit note", AdjustmentBody(setup.DebitOfInvoice, credit, valid)),
            ("installments on another reason", AdjustmentBody(setup.CreditOfInvoice, credit, valid, reason: "01", lines: oneLine)),
        };
        foreach (var (name, body) in cases)
        {
            var response = await PostNoteAsync(setup.Owner, body);
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{name}: {response.StatusCode}");
            Assert.Equal("SF-BIL-006", ProblemCode(await response.Content.ReadAsStringAsync()));
        }

        // Nothing was numbered: the first valid adjustment is number 1 of the series, and a repeated key returns the same note.
        var first = (await (await PostNoteAsync(setup.Owner, AdjustmentBody(setup.CreditOfInvoice, credit, valid), "adjust-key-001")).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        var again = (await (await PostNoteAsync(setup.Owner, AdjustmentBody(setup.CreditOfInvoice, credit, valid), "adjust-key-001")).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(1, first.Number);
        Assert.Equal(first.Id, again.Id);
    }

    [Fact]
    public async Task A_cash_invoice_is_unchanged_and_a_credit_invoice_is_idempotent_like_any_other()
    {
        var setup = await NewTenantAsync("Credit Idempotency SAC");
        var cash = (await (await PostAsync(setup.Owner, Body(setup.Invoice, false, null))).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Null((await setup.Owner.GetFromJsonAsync<DocumentDto>($"/api/v1/documents/{cash.Id}", ApiFixture.JsonOptions))!.Installments);

        var plan = Body(setup.Invoice, false, [Cuota(236m, Day(30))]);
        var a = (await (await PostAsync(setup.Owner, plan, "credit-key-0001")).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        var b = (await (await PostAsync(setup.Owner, plan, "credit-key-0001")).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;

        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.Number, b.Number);
        Assert.Single(b.Installments!);

        // The same key with a different plan is a conflict, not a second document.
        var changed = await PostAsync(setup.Owner, Body(setup.Invoice, false, [Cuota(236m, Day(45))]), "credit-key-0001");
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
    }
}
