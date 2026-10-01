using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Application;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;

namespace SecureFact.Security.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class VoidApiTests(ApiFixture api)
{
    private static int _rucCounter = 21_000_000;

    private sealed record Setup(Guid TenantId, HttpClient Owner, CompanyDto Company, SeriesDto Invoice, SeriesDto Receipt, SeriesDto CreditOfInvoice, SeriesDto CreditOfReceipt);

    private sealed class OffsetClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }

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
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = "Sol-Clave-void-1" })).StatusCode);

        async Task<SeriesDto> SeriesAsync(string type, string code) =>
            (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = type, code })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        return new Setup(tenantId, owner, company, await SeriesAsync("01", "F001"), await SeriesAsync("03", "B001"), await SeriesAsync("07", "FC01"), await SeriesAsync("07", "BC01"));
    }

    private static async Task<DocumentDto> IssueAsync(HttpClient client, SeriesDto series, bool receipt)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
        {
            Content = JsonContent.Create(new
            {
                seriesId = series.Id,
                issueDate = Iso(TodayInLima()),
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

    private static async Task<DocumentDto> NoteAsync(HttpClient client, SeriesDto series, DocumentDto referenced)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notes")
        {
            Content = JsonContent.Create(new
            {
                seriesId = series.Id,
                referencedDocumentId = referenced.Id,
                issueDate = Iso(TodayInLima()),
                reasonCode = "01",
                reason = "Anulación de la operación",
                lines = new[] { new { description = "Servicio de consultoría", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10" } } },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
    }

    private static async Task<HttpResponseMessage> TryNoteAsync(HttpClient client, SeriesDto series, DocumentDto referenced)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notes")
        {
            Content = JsonContent.Create(new
            {
                seriesId = series.Id,
                referencedDocumentId = referenced.Id,
                issueDate = Iso(TodayInLima()),
                reasonCode = "01",
                reason = "Anulación de la operación",
                lines = new[] { new { description = "Servicio de consultoría", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10" } } },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    private static async Task<ElectronicDocumentDto> PrepareAsync(HttpClient client, Guid documentId)
    {
        var response = await client.PostAsync($"/api/v1/documents/{documentId}/electronic", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
    }

    private async Task<ElectronicDocumentDto> AcceptedAsync(Setup setup, DocumentDto document, string reference)
    {
        var electronic = await PrepareAsync(setup.Owner, document.Id);
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, reference)));
        var sent = await setup.Owner.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/send", null);
        var dto = (await sent.Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(EDocumentState.Accepted, dto.State);
        return dto;
    }

    private static Task<HttpResponseMessage> VoidAsync(Setup setup, params (Guid DocumentId, string Reason)[] items) =>
        VoidAsync(setup.Owner, setup.Company.Id, items);

    private static Task<HttpResponseMessage> VoidAsync(HttpClient client, Guid companyId, params (Guid DocumentId, string Reason)[] items) =>
        client.PostAsJsonAsync("/api/v1/voids", new { companyId, items = items.Select(i => new { documentId = i.DocumentId, reason = i.Reason }) });

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private async Task FinishAsync(Setup setup, SummaryDto communication, string ticket, string code = "0")
    {
        api.Sunat.EnqueueSummary(ChannelReply.Issued(ticket));
        Assert.Equal(HttpStatusCode.OK, (await setup.Owner.PostAsync($"/api/v1/electronic-documents/{communication.Document.Id}/send", null)).StatusCode);
        api.Sunat.EnqueueStatus(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, communication.Document.FileBaseName[(setup.Company.Ruc.Length + 1)..], code, code == "0" ? "ha sido aceptada" : "rechazada")));
        Assert.Equal(HttpStatusCode.OK, (await setup.Owner.PostAsync($"/api/v1/electronic-documents/{communication.Document.Id}/poll", null)).StatusCode);
    }

    // ---------- creating ----------

    [Fact]
    public async Task An_accepted_invoice_and_note_of_an_invoice_can_be_voided_in_a_signed_communication()
    {
        var setup = await NewTenantAsync("Void Create SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var note = await NoteAsync(setup.Owner, setup.CreditOfInvoice, invoice);
        var invoiceElectronic = await AcceptedAsync(setup, invoice, $"{invoice.Series}-{invoice.Number}");
        var noteElectronic = await AcceptedAsync(setup, note, $"{note.Series}-{note.Number}");

        var response = await VoidAsync(setup, (invoice.Id, "Error en la emisión"), (note.Id, "Se anula junto con la factura"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var communication = Assert.Single((await response.Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);
        Assert.Equal("RA", communication.Document.DocumentTypeCode);
        Assert.Equal(EDocumentState.ReadyToSend, communication.Document.State);
        Assert.Equal($"{setup.Company.Ruc}-RA-{TodayInLima():yyyyMMdd}-1", communication.Document.FileBaseName);
        Assert.Equal(TodayInLima(), communication.ReferenceDate);
        Assert.Equal(2, communication.ElectronicDocumentIds.Count);

        var xml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{communication.Document.Id}/xml");
        Assert.True(new XmlDsigSigner().Verify(xml).Value.IsValid);
        Assert.Contains("<sac:DocumentSerialID>F001</sac:DocumentSerialID>", xml, StringComparison.Ordinal);
        Assert.Contains("<sac:DocumentSerialID>FC01</sac:DocumentSerialID>", xml, StringComparison.Ordinal);
        Assert.Contains("<sac:VoidReasonDescription>Error en la emisión</sac:VoidReasonDescription>", xml, StringComparison.Ordinal);
        Assert.Contains(invoiceElectronic.Id, communication.ElectronicDocumentIds);
        Assert.Contains(noteElectronic.Id, communication.ElectronicDocumentIds);

        var read = (await setup.Owner.GetFromJsonAsync<SummaryDto>($"/api/v1/voids/{communication.Document.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal(communication.ElectronicDocumentIds, read.ElectronicDocumentIds);
    }

    [Fact]
    public async Task A_document_is_voided_only_after_sunat_accepts_the_communication()
    {
        var setup = await NewTenantAsync("Void Accept SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var electronic = await AcceptedAsync(setup, invoice, $"{invoice.Series}-{invoice.Number}");
        var communication = Assert.Single((await (await VoidAsync(setup, (invoice.Id, "Error en la emisión"))).Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);
        Assert.False((await setup.Owner.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/electronic-documents/{electronic.Id}", ApiFixture.JsonOptions))!.Voided);

        await FinishAsync(setup, communication, "T-1");

        var voided = (await setup.Owner.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/electronic-documents/{electronic.Id}", ApiFixture.JsonOptions))!;
        Assert.True(voided.Voided);
        Assert.Equal(EDocumentState.Accepted, voided.State); // its own answer never changes
        Assert.True((await setup.Owner.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/documents/{invoice.Id}/electronic", ApiFixture.JsonOptions))!.Voided);
        Assert.Equal(EDocumentState.Accepted, (await setup.Owner.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/electronic-documents/{communication.Document.Id}", ApiFixture.JsonOptions))!.State);

        var again = await VoidAsync(setup, (invoice.Id, "Otra vez"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("SF-CPE-011", await ProblemCodeAsync(again));
    }

    [Fact]
    public async Task A_rejected_communication_frees_its_documents_to_be_voided_again()
    {
        var setup = await NewTenantAsync("Void Reject SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var electronic = await AcceptedAsync(setup, invoice, $"{invoice.Series}-{invoice.Number}");
        var first = Assert.Single((await (await VoidAsync(setup, (invoice.Id, "Error en la emisión"))).Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);

        var pending = await VoidAsync(setup, (invoice.Id, "Mientras tanto"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, pending.StatusCode); // already in a pending communication

        await FinishAsync(setup, first, "T-1", "2323");
        Assert.False((await setup.Owner.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/electronic-documents/{electronic.Id}", ApiFixture.JsonOptions))!.Voided);

        var second = Assert.Single((await (await VoidAsync(setup, (invoice.Id, "Segundo intento"))).Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);
        Assert.EndsWith("-2", second.Document.FileBaseName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_documents_with_an_accepted_cdr_are_voidable()
    {
        var setup = await NewTenantAsync("Void Guards SAC");
        var pending = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        await PrepareAsync(setup.Owner, pending.Id);
        var rejected = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var rejectedElectronic = await PrepareAsync(setup.Owner, rejected.Id);
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, $"{rejected.Series}-{rejected.Number}", "2047", "rechazada")));
        await setup.Owner.PostAsync($"/api/v1/electronic-documents/{rejectedElectronic.Id}/send", null);
        var receipt = await IssueAsync(setup.Owner, setup.Receipt, receipt: true);
        await PrepareAsync(setup.Owner, receipt.Id);
        var receiptNote = await NoteAsync(setup.Owner, setup.CreditOfReceipt, receipt);
        await PrepareAsync(setup.Owner, receiptNote.Id);
        var unprepared = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);

        foreach (var (name, id) in new[] { ("not sent yet", pending.Id), ("rejected", rejected.Id), ("receipt not informed", receipt.Id), ("note of a receipt not informed", receiptNote.Id) })
        {
            var response = await VoidAsync(setup, (id, "Error en la emisión"));
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{name}: {response.StatusCode}");
            Assert.Equal("SF-CPE-011", await ProblemCodeAsync(response));
        }

        var noElectronic = await VoidAsync(setup, (unprepared.Id, "Error en la emisión"));
        Assert.Equal(HttpStatusCode.NotFound, noElectronic.StatusCode);
        Assert.Empty(api.Sunat.SummaryCalls);
    }

    [Fact]
    public async Task Invalid_requests_are_refused()
    {
        var setup = await NewTenantAsync("Void Invalid SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        await AcceptedAsync(setup, invoice, $"{invoice.Series}-{invoice.Number}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await VoidAsync(setup)).StatusCode);
        var duplicated = await VoidAsync(setup, (invoice.Id, "Error en la emisión"), (invoice.Id, "Error en la emisión"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicated.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await VoidAsync(setup, (invoice.Id, " "))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await VoidAsync(setup, (invoice.Id, "x\ny z"))).StatusCode);
    }

    [Fact]
    public async Task A_document_older_than_seven_days_cannot_be_voided()
    {
        var setup = await NewTenantAsync("Void Old SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var electronic = await AcceptedAsync(setup, invoice, $"{invoice.Series}-{invoice.Number}");

        // Billing cannot issue an old invoice, so the age is faked by moving the clock of the service: today + 8 days.
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<SecureFact.Platform.Tenancy.DataScope>().UseTenant(new SecureFact.SharedKernel.Domain.TenantId(setup.TenantId));
        var late = ActivatorUtilities.CreateInstance<VoidService>(scope.ServiceProvider, (TimeProvider)new OffsetClock(TimeSpan.FromDays(8)));

        var result = await late.CreateAsync(new CreateVoidRequest(setup.Company.Id, [new VoidItem(invoice.Id, "Error en la emisión")]), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("SF-CPE-011", result.Error.Code);
        Assert.Contains("7 días", result.Error.Detail, StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, electronic.Id);
    }

    // ---------- receipts: a summary line with status 3 ----------

    /// <summary>Reports the receipts of today (then, in a later call, their notes) in daily summaries and gets them accepted: SUNAT must have informed them before they can be voided.</summary>
    private async Task AcceptReceiptsAsync(Setup setup, string ticket)
    {
        var created = await setup.Owner.PostAsJsonAsync("/api/v1/summaries", new { companyId = setup.Company.Id, referenceDate = Iso(TodayInLima()) });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        foreach (var summary in (await created.Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!)
        {
            await FinishAsync(setup, summary, ticket);
        }
    }

    private static async Task<ElectronicDocumentDto> ElectronicOfAsync(HttpClient client, Guid documentId) =>
        (await client.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/documents/{documentId}/electronic", ApiFixture.JsonOptions))!;

    [Fact]
    public async Task A_receipt_and_its_note_are_voided_with_a_summary_line_of_status_3()
    {
        var setup = await NewTenantAsync("Void Receipt SAC");
        var receipt = await IssueAsync(setup.Owner, setup.Receipt, receipt: true);
        var note = await NoteAsync(setup.Owner, setup.CreditOfReceipt, receipt);
        await AcceptReceiptsAsync(setup, "T-1"); // the receipt
        await AcceptReceiptsAsync(setup, "T-2"); // its note, once the receipt is informed
        Assert.Equal(EDocumentState.Accepted, (await ElectronicOfAsync(setup.Owner, receipt.Id)).State);
        Assert.Equal(EDocumentState.Accepted, (await ElectronicOfAsync(setup.Owner, note.Id)).State);

        var response = await VoidAsync(setup, (receipt.Id, "Operación anulada"), (note.Id, "Se anula con la boleta"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var voidSummary = Assert.Single((await response.Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);
        Assert.Equal("RC", voidSummary.Document.DocumentTypeCode); // a summary, not a communication
        Assert.Equal(2, voidSummary.ElectronicDocumentIds.Count);
        var xml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{voidSummary.Document.Id}/xml");
        Assert.True(new XmlDsigSigner().Verify(xml).Value.IsValid);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(xml, "<cbc:ConditionCode>3</cbc:ConditionCode>", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain("<cbc:ConditionCode>1</cbc:ConditionCode>", xml, StringComparison.Ordinal);
        Assert.Contains($"<cbc:ID>B001-{receipt.Number}</cbc:ID>", xml, StringComparison.Ordinal);
        Assert.Contains($"<cbc:ID>BC01-{note.Number}</cbc:ID>", xml, StringComparison.Ordinal);

        // Nothing changes until SUNAT accepts the summary; then both are voided but keep their own final answer.
        Assert.False((await ElectronicOfAsync(setup.Owner, receipt.Id)).Voided);
        await FinishAsync(setup, voidSummary, "T-3");
        var voided = await ElectronicOfAsync(setup.Owner, receipt.Id);
        Assert.True(voided.Voided);
        Assert.Equal(EDocumentState.Accepted, voided.State);
        Assert.True((await ElectronicOfAsync(setup.Owner, note.Id)).Voided);

        var read = (await setup.Owner.GetFromJsonAsync<SummaryDto>($"/api/v1/voids/{voidSummary.Document.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal(voidSummary.ElectronicDocumentIds, read.ElectronicDocumentIds);
        var again = await VoidAsync(setup, (receipt.Id, "Otra vez"));
        Assert.Equal("SF-CPE-011", await ProblemCodeAsync(again));
    }

    [Fact]
    public async Task A_rejected_summary_of_status_3_frees_the_receipt_which_itself_is_never_touched()
    {
        var setup = await NewTenantAsync("Void Receipt Reject SAC");
        var receipt = await IssueAsync(setup.Owner, setup.Receipt, receipt: true);
        await AcceptReceiptsAsync(setup, "T-1");
        var first = Assert.Single((await (await VoidAsync(setup, (receipt.Id, "Operación anulada"))).Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await VoidAsync(setup, (receipt.Id, "Mientras tanto"))).StatusCode); // already in a pending file

        await FinishAsync(setup, first, "T-2", "2513");

        var electronic = await ElectronicOfAsync(setup.Owner, receipt.Id);
        Assert.False(electronic.Voided);
        Assert.Equal(EDocumentState.Accepted, electronic.State);
        var second = Assert.Single((await (await VoidAsync(setup, (receipt.Id, "Segundo intento"))).Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);
        Assert.EndsWith("-3", second.Document.FileBaseName, StringComparison.Ordinal);

        // The ordinary summary never picks the already reported receipt up again, and the pending void does not count as a report.
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Owner.PostAsJsonAsync("/api/v1/summaries", new { companyId = setup.Company.Id, referenceDate = Iso(TodayInLima()) })).StatusCode);
    }

    [Fact]
    public async Task A_request_with_invoices_and_receipts_produces_a_communication_and_a_summary()
    {
        var setup = await NewTenantAsync("Void Mixed SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        await AcceptedAsync(setup, invoice, $"{invoice.Series}-{invoice.Number}");
        var receipt = await IssueAsync(setup.Owner, setup.Receipt, receipt: true);
        await AcceptReceiptsAsync(setup, "T-1");

        var response = await VoidAsync(setup, (invoice.Id, "Error en la emisión"), (receipt.Id, "Operación anulada"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var files = (await response.Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!;
        Assert.Equal(["RA", "RC"], files.Select(f => f.Document.DocumentTypeCode).OrderBy(t => t, StringComparer.Ordinal).ToArray());
        Assert.All(files, f => Assert.Single(f.ElectronicDocumentIds));
    }

    [Fact]
    public async Task An_ordinary_summary_is_not_a_void_record()
    {
        var setup = await NewTenantAsync("Void Not A Void SAC");
        await IssueAsync(setup.Owner, setup.Receipt, receipt: true);
        var summary = Assert.Single((await (await setup.Owner.PostAsJsonAsync("/api/v1/summaries", new { companyId = setup.Company.Id, referenceDate = Iso(TodayInLima()) })).Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);

        Assert.Equal(HttpStatusCode.NotFound, (await setup.Owner.GetAsync($"/api/v1/voids/{summary.Document.Id}")).StatusCode);
    }

    // ---------- printed representation ----------

    private static string PdfContent(byte[] pdf)
    {
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        var content = new System.Text.StringBuilder();
        foreach (System.Text.RegularExpressions.Match stream in System.Text.RegularExpressions.Regex.Matches(text, "stream\r?\n(?<body>.*?)\r?\nendstream", System.Text.RegularExpressions.RegexOptions.Singleline, TimeSpan.FromSeconds(5)))
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

    [Fact]
    public async Task The_pdf_of_a_voided_document_says_ANULADO_only_once_sunat_accepted_the_voiding()
    {
        var setup = await NewTenantAsync("Void Pdf SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var electronic = await AcceptedAsync(setup, invoice, $"{invoice.Series}-{invoice.Number}");
        Assert.DoesNotContain("(ANULADO)", PdfContent(await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf")), StringComparison.Ordinal);

        var communication = Assert.Single((await (await VoidAsync(setup, (invoice.Id, "Error en la emisión"))).Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);
        Assert.DoesNotContain("(ANULADO)", PdfContent(await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf")), StringComparison.Ordinal); // still pending

        await FinishAsync(setup, communication, "T-1");
        Assert.Contains("(ANULADO)", PdfContent(await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf")), StringComparison.Ordinal);
    }

    // ---------- notes on voided documents ----------

    [Fact]
    public async Task A_note_cannot_modify_an_invoice_that_is_voided_or_being_voided_until_the_request_is_rejected()
    {
        var setup = await NewTenantAsync("Void Notes Invoice SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        await AcceptedAsync(setup, invoice, $"{invoice.Series}-{invoice.Number}");
        Assert.Equal(HttpStatusCode.Created, (await TryNoteAsync(setup.Owner, setup.CreditOfInvoice, invoice)).StatusCode); // fine while it stands

        var first = Assert.Single((await (await VoidAsync(setup, (invoice.Id, "Error en la emisión"))).Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);
        var pending = await TryNoteAsync(setup.Owner, setup.CreditOfInvoice, invoice);
        Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        Assert.Equal("SF-BIL-011", await ProblemCodeAsync(pending));

        await FinishAsync(setup, first, "T-1", "2323"); // rejected: the invoice stands again
        Assert.Equal(HttpStatusCode.Created, (await TryNoteAsync(setup.Owner, setup.CreditOfInvoice, invoice)).StatusCode);

        var second = Assert.Single((await (await VoidAsync(setup, (invoice.Id, "Segundo intento"))).Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);
        await FinishAsync(setup, second, "T-2");
        Assert.Equal("SF-BIL-011", await ProblemCodeAsync(await TryNoteAsync(setup.Owner, setup.CreditOfInvoice, invoice)));
    }

    [Fact]
    public async Task A_note_cannot_modify_a_voided_receipt()
    {
        var setup = await NewTenantAsync("Void Notes Receipt SAC");
        var receipt = await IssueAsync(setup.Owner, setup.Receipt, receipt: true);
        await AcceptReceiptsAsync(setup, "T-1");
        var voidSummary = Assert.Single((await (await VoidAsync(setup, (receipt.Id, "Operación anulada"))).Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);
        await FinishAsync(setup, voidSummary, "T-2");

        var note = await TryNoteAsync(setup.Owner, setup.CreditOfReceipt, receipt);

        Assert.Equal(HttpStatusCode.Conflict, note.StatusCode);
        Assert.Equal("SF-BIL-011", await ProblemCodeAsync(note));
    }

    // ---------- sending ----------

    [Fact]
    public async Task The_communication_is_sent_with_send_summary_and_its_cdr_must_name_it()
    {
        var setup = await NewTenantAsync("Void Send SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        await AcceptedAsync(setup, invoice, $"{invoice.Series}-{invoice.Number}");
        var communication = Assert.Single((await (await VoidAsync(setup, (invoice.Id, "Error en la emisión"))).Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);
        var callsBefore = api.Sunat.Calls.Count;

        api.Sunat.EnqueueSummary(ChannelReply.Issued("T-9"));
        var sent = (await (await setup.Owner.PostAsync($"/api/v1/electronic-documents/{communication.Document.Id}/send", null)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;

        Assert.Equal(EDocumentState.AwaitingTicket, sent.State);
        Assert.Equal("T-9", sent.Ticket);
        var call = Assert.Single(api.Sunat.SummaryCalls);
        Assert.Equal(communication.Document.FileBaseName + ".zip", call.ZipFileName);
        Assert.Equal(callsBefore, api.Sunat.Calls.Count); // never sendBill

        api.Sunat.EnqueueStatus(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, "RA-20200101-9")));
        var mismatch = (await (await setup.Owner.PostAsync($"/api/v1/electronic-documents/{communication.Document.Id}/poll", null)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(EDocumentState.Failed, mismatch.State);
        Assert.Equal("SF-CPE-006", mismatch.LastErrorCode);
    }

    [Fact]
    public async Task The_worker_sends_and_follows_voided_communications()
    {
        var setup = await NewTenantAsync("Void Worker SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        var electronic = await AcceptedAsync(setup, invoice, $"{invoice.Series}-{invoice.Number}");
        var communication = Assert.Single((await (await VoidAsync(setup, (invoice.Id, "Error en la emisión"))).Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);
        var processor = new CpeWorkProcessor(api.Services.GetRequiredService<IServiceScopeFactory>(), new OffsetClock(TimeSpan.Zero), NullLogger<CpeWorkProcessor>.Instance);

        api.Sunat.EnqueueSummary(ChannelReply.Issued("T-W"));
        Assert.Equal(1, (await processor.RunOnceAsync(CancellationToken.None, setup.TenantId)).Sent);

        api.Sunat.EnqueueStatus(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, communication.Document.FileBaseName[(setup.Company.Ruc.Length + 1)..])));
        var later = new CpeWorkProcessor(api.Services.GetRequiredService<IServiceScopeFactory>(), new OffsetClock(TimeSpan.FromMinutes(2)), NullLogger<CpeWorkProcessor>.Instance);
        Assert.Equal(1, (await later.RunOnceAsync(CancellationToken.None, setup.TenantId)).Polled);

        Assert.True((await setup.Owner.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/electronic-documents/{electronic.Id}", ApiFixture.JsonOptions))!.Voided);
    }

    // ---------- security ----------

    [Fact]
    public async Task Voiding_is_isolated_between_tenants_guarded_by_role_and_the_record_stays_immutable()
    {
        var a = await NewTenantAsync("Void Iso A SAC");
        var b = await NewTenantAsync("Void Iso B SAC");
        var invoice = await IssueAsync(a.Owner, a.Invoice, receipt: false);
        await AcceptedAsync(a, invoice, $"{invoice.Series}-{invoice.Number}");

        var foreignCompany = await VoidAsync(b.Owner, a.Company.Id, (invoice.Id, "Error en la emisión"));
        Assert.Equal(HttpStatusCode.NotFound, foreignCompany.StatusCode);
        var foreignDocument = await VoidAsync(b, (invoice.Id, "Error en la emisión"));
        Assert.Equal(HttpStatusCode.NotFound, foreignDocument.StatusCode);

        var sales = await ApiFixture.CreateUserAsync(a.Owner, Roles.Sales, a.TenantId);
        using var salesClient = api.ClientFor(await api.LoginOkAsync(sales.Email, sales.Password));
        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await VoidAsync(salesClient, a.Company.Id, (invoice.Id, "Error en la emisión"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await VoidAsync(anonymous, a.Company.Id, (invoice.Id, "Error en la emisión"))).StatusCode);

        var communication = Assert.Single((await (await VoidAsync(a, (invoice.Id, "Error en la emisión"))).Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/voids/{communication.Document.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await salesClient.GetAsync($"/api/v1/voids/{communication.Document.Id}")).StatusCode);

        await using var connection = new NpgsqlConnection(api.Postgres.OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"UPDATE cpe.summary_item SET reason = 'cambiado' WHERE summary_id = '{communication.Document.Id}'", connection);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task Two_simultaneous_requests_void_a_document_only_once()
    {
        var setup = await NewTenantAsync("Void Race SAC");
        var invoice = await IssueAsync(setup.Owner, setup.Invoice, receipt: false);
        await AcceptedAsync(setup, invoice, $"{invoice.Series}-{invoice.Number}");

        var responses = await Task.WhenAll(VoidAsync(setup, (invoice.Id, "Error en la emisión")), VoidAsync(setup, (invoice.Id, "Error en la emisión")));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        await using var connection = new NpgsqlConnection(api.Postgres.OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM cpe.summary_item i JOIN cpe.electronic_document d ON d.id = i.summary_id WHERE d.company_id = '{setup.Company.Id}' AND d.document_type_code = 'RA' AND i.released_at IS NULL", connection);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }
}
