using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;

namespace SecureFact.Security.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class NotesApiTests(ApiFixture api)
{
    private static int _rucCounter = 17_000_000;

    private sealed record Setup(Guid TenantId, HttpClient Owner, CompanyDto Company, SeriesDto Invoice, SeriesDto Receipt, SeriesDto CreditOfInvoice, SeriesDto DebitOfInvoice, SeriesDto CreditOfReceipt);

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
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = "Sol-Clave-notes-1" })).StatusCode);

        async Task<SeriesDto> SeriesAsync(string type, string code) =>
            (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = type, code })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        return new Setup(
            tenantId, owner, company, await SeriesAsync("01", "F001"), await SeriesAsync("03", "B001"),
            await SeriesAsync("07", "FC01"), await SeriesAsync("08", "FD01"), await SeriesAsync("07", "BC01"));
    }

    private static async Task<DocumentDto> IssueAsync(HttpClient client, SeriesDto series, bool receipt, decimal quantity = 2m, DateOnly? date = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
        {
            Content = JsonContent.Create(new
            {
                seriesId = series.Id,
                issueDate = Iso(date ?? TodayInLima()),
                currency = "PEN",
                buyer = receipt
                    ? new { documentTypeCode = "1", documentNumber = "12345678", name = "Persona Natural" }
                    : new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
                lines = new[] { new { description = "Servicio de consultoría", unitCode = "ZZ", tax = new { quantity, unitValue = 100m, igvAffectationCode = "10" } } },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
    }

    private static object NoteBody(SeriesDto series, DocumentDto referenced, string reason = "01", string text = "Anulación de la operación", decimal quantity = 1m, DateOnly? date = null) => new
    {
        seriesId = series.Id,
        referencedDocumentId = referenced.Id,
        issueDate = Iso(date ?? TodayInLima()),
        reasonCode = reason,
        reason = text,
        lines = new[] { new { description = "Servicio de consultoría", unitCode = "ZZ", tax = new { quantity, unitValue = 100m, igvAffectationCode = "10" } } },
    };

    private static Task<HttpResponseMessage> PostNoteAsync(HttpClient client, object body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notes") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return client.SendAsync(request);
    }

    private static async Task<DocumentDto> NoteOkAsync(HttpClient client, object body)
    {
        var response = await PostNoteAsync(client, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
    }

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private static async Task<ElectronicDocumentDto> PrepareAsync(HttpClient client, Guid documentId)
    {
        var response = await client.PostAsync($"/api/v1/documents/{documentId}/electronic", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
    }

    // ---------- issuing ----------

    [Fact]
    public async Task A_credit_note_is_numbered_and_carries_what_it_modifies_and_why()
    {
        var setup = await NewTenantAsync("Notes Credit SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);

        var note = await NoteOkAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, "07", "Devolución por ítem"));

        Assert.Equal("07", note.DocumentTypeCode);
        Assert.Equal("FC01", note.Series);
        Assert.Equal(1, note.Number);
        Assert.Equal(invoice.Currency, note.Currency);
        Assert.Equal(invoice.Buyer.DocumentNumber, note.Buyer.DocumentNumber);
        Assert.Equal(118m, note.Totals.PayableAmount);
        Assert.Equal(new NoteInfo("07", "Devolución por ítem", invoice.Id, "01", "F001", invoice.Number), note.Note);
        Assert.Null(invoice.Note);

        var read = (await setup.Owner.GetFromJsonAsync<DocumentDto>($"/api/v1/documents/{note.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal(note.Note, read.Note);
        var second = await NoteOkAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, "01"));
        Assert.Equal(2, second.Number);
    }

    [Fact]
    public async Task A_debit_note_is_numbered_in_its_own_series_and_may_exceed_the_original()
    {
        var setup = await NewTenantAsync("Notes Debit SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false, quantity: 1m);

        var note = await NoteOkAsync(setup.Owner, NoteBody(setup.DebitOfInvoice, invoice, "02", "Aumento en el valor", quantity: 5m));

        Assert.Equal("08", note.DocumentTypeCode);
        Assert.Equal("FD01", note.Series);
        Assert.Equal(590m, note.Totals.PayableAmount);
    }

    [Fact]
    public async Task A_note_of_a_receipt_uses_a_B_series_and_the_receipts_buyer()
    {
        var setup = await NewTenantAsync("Notes Receipt SAC");
        var receipt = await IssueAsync(setup.Owner, setup.Receipt, receipt: true);

        var note = await NoteOkAsync(setup.Owner, NoteBody(setup.CreditOfReceipt, receipt, "06", "Devolución total", quantity: 2m));

        Assert.Equal("BC01", note.Series);
        Assert.Equal("1", note.Buyer.DocumentTypeCode);
        Assert.Equal(new NoteInfo("06", "Devolución total", receipt.Id, "03", "B001", receipt.Number), note.Note);
    }

    [Fact]
    public async Task Replaying_a_note_returns_the_same_note_and_changing_it_is_a_conflict()
    {
        var setup = await NewTenantAsync("Notes Idempotent SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var key = Guid.NewGuid().ToString("N");

        var first = (await (await PostNoteAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice), key)).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        var replay = (await (await PostNoteAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice), key)).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        var changed = await PostNoteAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, "02"), key);

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal("SF-BIL-008", await ProblemCodeAsync(changed));
    }

    [Fact]
    public async Task A_credit_note_cannot_exceed_the_document_it_modifies()
    {
        var setup = await NewTenantAsync("Notes Cap SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false, quantity: 2m);

        var tooMuch = await PostNoteAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, quantity: 3m));
        var exact = await PostNoteAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, quantity: 2m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMuch.StatusCode);
        Assert.Equal("SF-BIL-010", await ProblemCodeAsync(tooMuch));
        Assert.Equal(HttpStatusCode.Created, exact.StatusCode);
    }

    [Fact]
    public async Task Invalid_notes_are_refused_and_burn_no_number()
    {
        var setup = await NewTenantAsync("Notes Invalid SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var yesterday = TodayInLima().AddDays(-1);

        var cases = new Dictionary<string, (HttpResponseMessage Response, string Code)>
        {
            ["reason 11 (export)"] = (await PostNoteAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, "11")), "SF-BIL-006"),
            ["reason 13"] = (await PostNoteAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, "13")), "SF-BIL-006"),
            ["debit reason 10"] = (await PostNoteAsync(setup.Owner, NoteBody(setup.DebitOfInvoice, invoice, "10")), "SF-BIL-006"),
            ["no reason text"] = (await PostNoteAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, text: " ")), "SF-BIL-006"),
            ["multi-line reason"] = (await PostNoteAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, text: "a\nb")), "SF-BIL-006"),
            ["receipt series for an invoice"] = (await PostNoteAsync(setup.Owner, NoteBody(setup.CreditOfReceipt, invoice)), "SF-BIL-006"),
            ["invoice series as a note series"] = (await PostNoteAsync(setup.Owner, NoteBody(setup.Invoice, invoice)), "SF-BIL-009"),
            ["dated before the original"] = (await PostNoteAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, date: yesterday)), "SF-BIL-006"),
            ["future date"] = (await PostNoteAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, date: TodayInLima().AddDays(2))), "SF-BIL-006"),
        };

        foreach (var (name, (response, code)) in cases)
        {
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{name}: {response.StatusCode}");
            Assert.Equal(code, await ProblemCodeAsync(response));
        }

        // Nothing was numbered by the refusals: the first valid note is number 1.
        Assert.Equal(1, (await NoteOkAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice))).Number);
    }

    [Fact]
    public async Task A_note_cannot_modify_another_note_or_a_document_of_another_company_or_tenant()
    {
        var a = await NewTenantAsync("Notes Iso A SAC");
        var b = await NewTenantAsync("Notes Iso B SAC");
        var invoice = await IssueAsync(a.Owner, a.Invoice, receipt: false);
        var note = await NoteOkAsync(a.Owner, NoteBody(a.CreditOfInvoice, invoice));

        var ofNote = await PostNoteAsync(a.Owner, NoteBody(a.CreditOfInvoice, note));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ofNote.StatusCode);

        var foreign = await PostNoteAsync(b.Owner, NoteBody(b.CreditOfInvoice, invoice));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("SF-BIL-007", await ProblemCodeAsync(foreign));

        var stolenSeries = await PostNoteAsync(b.Owner, NoteBody(a.CreditOfInvoice, invoice));
        Assert.Equal(HttpStatusCode.NotFound, stolenSeries.StatusCode);
    }

    [Fact]
    public async Task Only_roles_that_create_documents_can_issue_notes()
    {
        var setup = await NewTenantAsync("Notes Rbac SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var readOnly = await ApiFixture.CreateUserAsync(setup.Owner, Roles.ReadOnly, setup.TenantId);
        using var readOnlyClient = api.ClientFor(await api.LoginOkAsync(readOnly.Email, readOnly.Password));
        using var anonymous = api.NewClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await PostNoteAsync(readOnlyClient, NoteBody(setup.CreditOfInvoice, invoice))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostNoteAsync(anonymous, NoteBody(setup.CreditOfInvoice, invoice))).StatusCode);
    }

    // ---------- accumulated credit ----------

    [Fact]
    public async Task Credit_notes_accumulate_and_a_document_cannot_be_credited_more_than_once()
    {
        var setup = await NewTenantAsync("Notes Accumulate SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false); // 2 x 100 + IGV = 236.00

        await NoteOkAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, quantity: 1m));
        await NoteOkAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, "07", "Devolución por ítem", quantity: 1m)); // 236.00 in total: exactly the invoice

        var third = await PostNoteAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, "07", "Otra devolución", quantity: 1m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, third.StatusCode);
        Assert.Equal("SF-BIL-010", await ProblemCodeAsync(third));
        Assert.Contains("236.00", await third.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A debit note is not credit, and another document has its own account.
        await NoteOkAsync(setup.Owner, NoteBody(setup.DebitOfInvoice, invoice, "02", "Aumento en el valor", quantity: 5m));
        var other = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        await NoteOkAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, other, quantity: 2m));

        // The same goes for a receipt, where a partial credit leaves only the rest.
        var receipt = await IssueAsync(setup.Owner, setup.Receipt, receipt: true, quantity: 3m);
        await NoteOkAsync(setup.Owner, NoteBody(setup.CreditOfReceipt, receipt, "06", "Devolución parcial", quantity: 1m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostNoteAsync(setup.Owner, NoteBody(setup.CreditOfReceipt, receipt, "06", "Devolución parcial", quantity: 3m))).StatusCode);
        await NoteOkAsync(setup.Owner, NoteBody(setup.CreditOfReceipt, receipt, "06", "Devolución del resto", quantity: 2m));
    }

    [Fact]
    public async Task A_refused_accumulation_takes_no_number_and_two_simultaneous_notes_fit_only_once()
    {
        var setup = await NewTenantAsync("Notes Accumulate Race SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);

        var responses = await Task.WhenAll(
            PostNoteAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, quantity: 2m)),
            PostNoteAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, quantity: 2m)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
        var created = (await responses.Single(r => r.StatusCode == HttpStatusCode.Created).Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(1, created.Number);

        // The refused one consumed no number: the series stops at 1.
        var series = (await setup.Owner.GetFromJsonAsync<List<SeriesDto>>($"/api/v1/series?companyId={setup.Company.Id}", ApiFixture.JsonOptions))!.Single(x => x.Id == setup.CreditOfInvoice.Id);
        Assert.Equal(1, series.LastNumber);
    }

    [Fact]
    public async Task A_credit_note_that_sunat_rejected_stops_counting_and_the_credit_is_available_again()
    {
        var setup = await NewTenantAsync("Notes Accumulate Rejected SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var first = await NoteOkAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, quantity: 2m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostNoteAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, quantity: 1m))).StatusCode);

        var invoiceElectronic = await PrepareAsync(setup.Owner, invoice.Id);
        var noteElectronic = await PrepareAsync(setup.Owner, first.Id);
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, "F001-1")));
        await setup.Owner.PostAsync($"/api/v1/electronic-documents/{invoiceElectronic.Id}/send", null);
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, "FC01-1", "2047", "rechazada")));
        var rejected = (await (await setup.Owner.PostAsync($"/api/v1/electronic-documents/{noteElectronic.Id}/send", null)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(EDocumentState.Rejected, rejected.State);

        await NoteOkAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, quantity: 2m));
    }

    [Fact]
    public async Task A_credit_note_that_was_voided_stops_counting()
    {
        var setup = await NewTenantAsync("Notes Accumulate Voided SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var first = await NoteOkAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, quantity: 2m));
        var invoiceElectronic = await PrepareAsync(setup.Owner, invoice.Id);
        var noteElectronic = await PrepareAsync(setup.Owner, first.Id);
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, "F001-1")));
        await setup.Owner.PostAsync($"/api/v1/electronic-documents/{invoiceElectronic.Id}/send", null);
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, "FC01-1")));
        await setup.Owner.PostAsync($"/api/v1/electronic-documents/{noteElectronic.Id}/send", null);

        var voided = await setup.Owner.PostAsJsonAsync("/api/v1/voids", new { companyId = setup.Company.Id, items = new[] { new { documentId = first.Id, reason = "Nota emitida por error" } } });
        Assert.Equal(HttpStatusCode.Created, voided.StatusCode);
        var communication = Assert.Single((await voided.Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);

        // Voiding is not final until SUNAT accepts it: the note still counts, and then it does not.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostNoteAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, quantity: 2m))).StatusCode);
        api.Sunat.EnqueueSummary(ChannelReply.Issued("T-1"));
        await setup.Owner.PostAsync($"/api/v1/electronic-documents/{communication.Document.Id}/send", null);
        api.Sunat.EnqueueStatus(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, communication.Document.FileBaseName[(setup.Company.Ruc.Length + 1)..])));
        await setup.Owner.PostAsync($"/api/v1/electronic-documents/{communication.Document.Id}/poll", null);

        await NoteOkAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, quantity: 2m));
    }

    // ---------- electronic document ----------

    [Fact]
    public async Task A_note_waits_until_the_document_it_modifies_is_accepted_then_is_sent_and_accepted()
    {
        var setup = await NewTenantAsync("Notes Pipeline SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var note = await NoteOkAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice));
        var invoiceElectronic = await PrepareAsync(setup.Owner, invoice.Id);
        var noteElectronic = await PrepareAsync(setup.Owner, note.Id);

        Assert.Equal("07", noteElectronic.DocumentTypeCode);
        Assert.Equal($"{setup.Company.Ruc}-07-FC01-1", noteElectronic.FileBaseName);
        var xml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{noteElectronic.Id}/xml");
        Assert.Contains("<cac:DiscrepancyResponse>", xml, StringComparison.Ordinal);
        Assert.Contains("<cbc:ReferenceID>F001-1</cbc:ReferenceID>", xml, StringComparison.Ordinal);
        Assert.True(new XmlDsigSigner().Verify(xml).Value.IsValid);

        // The invoice is not accepted yet: the note steps aside and SUNAT is not called.
        var early = await setup.Owner.PostAsync($"/api/v1/electronic-documents/{noteElectronic.Id}/send", null);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("SF-CPE-010", await ProblemCodeAsync(early));
        Assert.Empty(api.Sunat.Calls);
        var waiting = (await setup.Owner.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/electronic-documents/{noteElectronic.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal((EDocumentState.ReadyToSend, 0), (waiting.State, waiting.Attempts));
        Assert.True(waiting.NextAttemptAt > DateTimeOffset.UtcNow);

        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, "F001-1")));
        Assert.Equal(EDocumentState.Accepted, (await (await setup.Owner.PostAsync($"/api/v1/electronic-documents/{invoiceElectronic.Id}/send", null)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!.State);

        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, "FC01-1")));
        var sent = (await (await setup.Owner.PostAsync($"/api/v1/electronic-documents/{noteElectronic.Id}/send", null)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(EDocumentState.Accepted, sent.State);
        Assert.Equal(2, api.Sunat.Calls.Count);
    }

    [Fact]
    public async Task A_rejected_original_keeps_its_notes_waiting_with_a_clear_reason()
    {
        var setup = await NewTenantAsync("Notes Rejected Original SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var note = await NoteOkAsync(setup.Owner, NoteBody(setup.DebitOfInvoice, invoice, "02"));
        var invoiceElectronic = await PrepareAsync(setup.Owner, invoice.Id);
        var noteElectronic = await PrepareAsync(setup.Owner, note.Id);
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, "F001-1", "2047", "rechazada")));
        await setup.Owner.PostAsync($"/api/v1/electronic-documents/{invoiceElectronic.Id}/send", null);

        var response = await setup.Owner.PostAsync($"/api/v1/electronic-documents/{noteElectronic.Id}/send", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("Rejected", body.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Single(api.Sunat.Calls);
    }

    [Fact]
    public async Task Notes_of_receipts_are_prepared_but_not_sent_one_by_one_yet()
    {
        var setup = await NewTenantAsync("Notes Receipt Send SAC");
        var receipt = await IssueAsync(setup.Owner, setup.Receipt, receipt: true);
        var note = await NoteOkAsync(setup.Owner, NoteBody(setup.CreditOfReceipt, receipt, "06", "Devolución total", quantity: 2m));
        var electronic = await PrepareAsync(setup.Owner, note.Id);

        var response = await setup.Owner.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/send", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("SF-CPE-002", await ProblemCodeAsync(response));
        Assert.Empty(api.Sunat.Calls);
    }

    [Fact]
    public async Task A_note_prints_with_the_document_it_modifies_and_its_reason()
    {
        var setup = await NewTenantAsync("Notes Pdf SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var note = await NoteOkAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice, "07", "Devolución por ítem"));
        var electronic = await PrepareAsync(setup.Owner, note.Id);

        var pdf = await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf");

        var text = System.Text.Encoding.Latin1.GetString(pdf);
        var content = new System.Text.StringBuilder();
        foreach (System.Text.RegularExpressions.Match stream in System.Text.RegularExpressions.Regex.Matches(text, @"stream
(?<body>.*?)
endstream", System.Text.RegularExpressions.RegexOptions.Singleline, TimeSpan.FromSeconds(5)))
        {
            using var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(System.Text.Encoding.Latin1.GetBytes(stream.Groups["body"].Value)), System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, System.Text.Encoding.Latin1);
            content.Append(reader.ReadToEnd());
        }

        foreach (var expected in new[] { "NOTA DE CRÉDITO ELECTRÓNICA", "FC01-1", "Factura electrónica F001-1", "Devolución por ítem", "S/ 118.00" })
        {
            Assert.Contains(expected, content.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_outbox_prepares_notes_like_any_other_document()
    {
        var setup = await NewTenantAsync("Notes Outbox SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var note = await NoteOkAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice));

        var report = await new SecureFact.Platform.Messaging.OutboxProcessor(
            api.Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SecureFact.Platform.Messaging.OutboxProcessor>.Instance)
            .RunOnceAsync(CancellationToken.None, setup.TenantId);

        Assert.Equal(2, report.Delivered); // the invoice and the note
        var electronic = (await setup.Owner.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/documents/{note.Id}/electronic", ApiFixture.JsonOptions))!;
        Assert.Equal("07", electronic.DocumentTypeCode);
    }

    [Fact]
    public async Task The_signed_reference_of_a_note_is_immutable_in_the_database()
    {
        var setup = await NewTenantAsync("Notes Frozen SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var electronic = await PrepareAsync(setup.Owner, (await NoteOkAsync(setup.Owner, NoteBody(setup.CreditOfInvoice, invoice))).Id);

        await using var connection = new NpgsqlConnection(api.Postgres.OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"UPDATE cpe.electronic_document SET reference_document_id = gen_random_uuid() WHERE id = '{electronic.Id}'", connection);

        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
    }
}
