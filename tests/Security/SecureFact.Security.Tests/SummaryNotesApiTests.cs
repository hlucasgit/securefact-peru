using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine.Application;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;

namespace SecureFact.Security.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class SummaryNotesApiTests(ApiFixture api)
{
    private static int _rucCounter = 19_000_000;

    private sealed record Setup(Guid TenantId, HttpClient Owner, CompanyDto Company, SeriesDto Invoice, SeriesDto Receipt, SeriesDto CreditOfReceipt, SeriesDto DebitOfReceipt, SeriesDto CreditOfInvoice);

    private sealed class OffsetClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }

    private CpeWorkProcessor Processor(TimeSpan? offset = null) =>
        new(api.Services.GetRequiredService<IServiceScopeFactory>(), new OffsetClock(offset ?? TimeSpan.Zero), NullLogger<CpeWorkProcessor>.Instance);

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
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = "Sol-Clave-rcnotes-1" })).StatusCode);

        async Task<SeriesDto> SeriesAsync(string type, string code) =>
            (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = type, code })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        return new Setup(
            tenantId, owner, company, await SeriesAsync("01", "F001"), await SeriesAsync("03", "B001"),
            await SeriesAsync("07", "BC01"), await SeriesAsync("08", "BD01"), await SeriesAsync("07", "FC01"));
    }

    private static async Task<DocumentDto> IssueAsync(HttpClient client, SeriesDto series, bool receipt, DateOnly? date = null)
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
                lines = new[] { new { description = "Servicio de consultoría", unitCode = "ZZ", tax = new { quantity = 2m, unitValue = 100m, igvAffectationCode = "10" } } },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
    }

    private static async Task<DocumentDto> NoteAsync(HttpClient client, SeriesDto series, DocumentDto referenced, DateOnly? date = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notes")
        {
            Content = JsonContent.Create(new
            {
                seriesId = series.Id,
                referencedDocumentId = referenced.Id,
                issueDate = Iso(date ?? TodayInLima()),
                reasonCode = series.DocumentTypeCode == "07" ? "01" : "02",
                reason = "Sustento de la nota",
                lines = new[] { new { description = "Servicio de consultoría", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10" } } },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
    }

    private static async Task<ElectronicDocumentDto> ElectronicOfAsync(HttpClient client, Guid documentId) =>
        (await client.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/documents/{documentId}/electronic", ApiFixture.JsonOptions))!;

    private static async Task<List<SummaryDto>> CreateSummariesAsync(HttpClient client, Guid companyId, DateOnly date)
    {
        var response = await client.PostAsJsonAsync("/api/v1/summaries", new { companyId, referenceDate = Iso(date) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!;
    }

    private async Task AcceptAsync(Setup setup, SummaryDto summary, string ticket)
    {
        api.Sunat.EnqueueSummary(ChannelReply.Issued(ticket));
        Assert.Equal(HttpStatusCode.OK, (await setup.Owner.PostAsync($"/api/v1/electronic-documents/{summary.Document.Id}/send", null)).StatusCode);
        api.Sunat.EnqueueStatus(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, summary.Document.FileBaseName[(setup.Company.Ruc.Length + 1)..])));
        var polled = await setup.Owner.PostAsync($"/api/v1/electronic-documents/{summary.Document.Id}/poll", null);
        Assert.Equal(EDocumentState.Accepted, (await polled.Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!.State);
    }

    [Fact]
    public async Task A_note_of_a_receipt_waits_for_the_receipt_to_be_informed_then_goes_in_a_later_summary()
    {
        var setup = await NewTenantAsync("RcNotes Wait SAC");
        var receipt = await IssueAsync(setup.Owner, setup.Receipt, receipt: true);
        var credit = await NoteAsync(setup.Owner, setup.CreditOfReceipt, receipt);
        var debit = await NoteAsync(setup.Owner, setup.DebitOfReceipt, receipt);
        var today = TodayInLima();

        // First summary: the receipt only. Its notes stay out until SUNAT has accepted the receipt (rule 2989).
        var first = Assert.Single(await CreateSummariesAsync(setup.Owner, setup.Company.Id, today));
        Assert.Single(first.ElectronicDocumentIds);
        Assert.Equal((await ElectronicOfAsync(setup.Owner, receipt.Id)).Id, first.ElectronicDocumentIds[0]);
        var xml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{first.Document.Id}/xml");
        Assert.DoesNotContain("BC01-1", xml, StringComparison.Ordinal);
        var stillWaiting = await setup.Owner.PostAsJsonAsync("/api/v1/summaries", new { companyId = setup.Company.Id, referenceDate = Iso(today) });
        Assert.Equal(HttpStatusCode.NotFound, stillWaiting.StatusCode); // nothing new to summarize yet

        await AcceptAsync(setup, first, "T-1");
        Assert.Equal(EDocumentState.Accepted, (await ElectronicOfAsync(setup.Owner, receipt.Id)).State);

        // Second summary: both notes, now that the receipt is informed.
        var second = Assert.Single(await CreateSummariesAsync(setup.Owner, setup.Company.Id, today));
        Assert.Equal(2, second.ElectronicDocumentIds.Count);
        Assert.EndsWith("-2", second.Document.FileBaseName, StringComparison.Ordinal);
        var secondXml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{second.Document.Id}/xml");
        Assert.Contains("<cbc:ID>BC01-1</cbc:ID>", secondXml, StringComparison.Ordinal);
        Assert.Contains("<cbc:ID>BD01-1</cbc:ID>", secondXml, StringComparison.Ordinal);
        Assert.Contains("<cbc:DocumentTypeCode>07</cbc:DocumentTypeCode>", secondXml, StringComparison.Ordinal);
        Assert.Contains("<cbc:DocumentTypeCode>08</cbc:DocumentTypeCode>", secondXml, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(secondXml, "<cac:BillingReference>", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2)));
        Assert.Contains($"<cbc:ID>B001-{receipt.Number}</cbc:ID>", secondXml, StringComparison.Ordinal);

        await AcceptAsync(setup, second, "T-2");
        Assert.Equal(EDocumentState.Accepted, (await ElectronicOfAsync(setup.Owner, credit.Id)).State);
        Assert.Equal(EDocumentState.Accepted, (await ElectronicOfAsync(setup.Owner, debit.Id)).State);
    }

    [Fact]
    public async Task Notes_of_invoices_are_never_summarized_and_a_rejected_summary_frees_its_notes()
    {
        var setup = await NewTenantAsync("RcNotes Invoice SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        await NoteAsync(setup.Owner, setup.CreditOfInvoice, invoice);

        var nothing = await setup.Owner.PostAsJsonAsync("/api/v1/summaries", new { companyId = setup.Company.Id, referenceDate = Iso(TodayInLima()) });
        Assert.Equal(HttpStatusCode.NotFound, nothing.StatusCode); // an invoice and its note go by sendBill, never in a summary

        var receipt = await IssueAsync(setup.Owner, setup.Receipt, receipt: true);
        var note = await NoteAsync(setup.Owner, setup.CreditOfReceipt, receipt);
        var first = Assert.Single(await CreateSummariesAsync(setup.Owner, setup.Company.Id, TodayInLima()));
        await AcceptAsync(setup, first, "T-1");
        var second = Assert.Single(await CreateSummariesAsync(setup.Owner, setup.Company.Id, TodayInLima()));

        // SUNAT rejects the second summary: the note was not judged and goes back to the queue.
        api.Sunat.EnqueueSummary(ChannelReply.Issued("T-2"));
        await setup.Owner.PostAsync($"/api/v1/electronic-documents/{second.Document.Id}/send", null);
        api.Sunat.EnqueueStatus(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, second.Document.FileBaseName[(setup.Company.Ruc.Length + 1)..], "2513", "Dato no cumple con formato")));
        await setup.Owner.PostAsync($"/api/v1/electronic-documents/{second.Document.Id}/poll", null);

        Assert.Equal(EDocumentState.ReadyToSend, (await ElectronicOfAsync(setup.Owner, note.Id)).State);
        var third = Assert.Single(await CreateSummariesAsync(setup.Owner, setup.Company.Id, TodayInLima()));
        Assert.EndsWith("-3", third.Document.FileBaseName, StringComparison.Ordinal);
        Assert.Equal(second.ElectronicDocumentIds, third.ElectronicDocumentIds);
    }

    [Fact]
    public async Task The_worker_brings_the_notes_of_closed_days_in_after_their_receipt_is_informed()
    {
        var setup = await NewTenantAsync("RcNotes Worker SAC");
        var yesterday = TodayInLima().AddDays(-1);
        var receipt = await IssueAsync(setup.Owner, setup.Receipt, receipt: true, yesterday);
        var note = await NoteAsync(setup.Owner, setup.CreditOfReceipt, receipt, yesterday);
        await setup.Owner.PostAsync($"/api/v1/documents/{receipt.Id}/electronic", null);
        await setup.Owner.PostAsync($"/api/v1/documents/{note.Id}/electronic", null);

        // Pass 1: the receipt's summary is created and sent; the note is left for later.
        api.Sunat.EnqueueSummary(ChannelReply.Issued("T-1"));
        var first = await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId);
        Assert.Equal((1, 1), (first.SummariesCreated, first.Sent));
        var firstName = Assert.Single(api.Sunat.SummaryCalls).ZipFileName[..^".zip".Length];

        // Pass 2 (ticket due): the receipt is accepted.
        api.Sunat.EnqueueStatus(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, firstName[(setup.Company.Ruc.Length + 1)..])));
        var second = await Processor(TimeSpan.FromMinutes(2)).RunOnceAsync(CancellationToken.None, setup.TenantId);
        Assert.Equal(1, second.Polled);
        Assert.Equal(EDocumentState.Accepted, (await ElectronicOfAsync(setup.Owner, receipt.Id)).State);

        // Pass 3: the note, now allowed, gets its own summary, which is sent and then accepted.
        api.Sunat.EnqueueSummary(ChannelReply.Issued("T-2"));
        var third = await Processor(TimeSpan.FromMinutes(3)).RunOnceAsync(CancellationToken.None, setup.TenantId);
        Assert.Equal((1, 1), (third.SummariesCreated, third.Sent));
        var secondName = api.Sunat.SummaryCalls.Last().ZipFileName[..^".zip".Length];
        Assert.NotEqual(firstName, secondName);
        api.Sunat.EnqueueStatus(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, secondName[(setup.Company.Ruc.Length + 1)..])));
        await Processor(TimeSpan.FromMinutes(6)).RunOnceAsync(CancellationToken.None, setup.TenantId);

        Assert.Equal(EDocumentState.Accepted, (await ElectronicOfAsync(setup.Owner, note.Id)).State);
    }
}
