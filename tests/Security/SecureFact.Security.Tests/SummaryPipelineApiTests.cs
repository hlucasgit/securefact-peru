using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Npgsql;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;

namespace SecureFact.Security.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class SummaryPipelineApiTests(ApiFixture api)
{
    private static int _rucCounter = 11_000_000;

    private sealed record Setup(Guid TenantId, HttpClient Owner, CompanyDto Company, SeriesDto InvoiceSeries, SeriesDto ReceiptSeries);

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

    private async Task<Setup> NewTenantAsync(string name, bool certificate = true)
    {
        api.Sunat.Reset();
        var tenantId = await api.CreateTenantAsync(name);
        using var admin = await api.AdminClientAsync();
        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        var owner = api.ClientFor(await api.LoginOkAsync(user.Email, user.Password));
        var ruc = NewRuc();
        var company = (await (await owner.PostAsJsonAsync("/api/v1/companies", new { ruc, details = new { legalName = "Emisora SAC", fiscalAddress = "Av. Larco 123", ubigeo = "150122" } }))
            .Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!;
        if (certificate)
        {
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/certificates", new { companyId = company.Id, pfxBase64 = PfxFor(ruc), password = "pw" })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = "Sol-Clave-summary-1" })).StatusCode);

        async Task<SeriesDto> SeriesAsync(string type, string code) =>
            (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = type, code })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        return new Setup(tenantId, owner, company, await SeriesAsync("01", "F001"), await SeriesAsync("03", "B001"));
    }

    private static async Task<DocumentDto> NewDocumentAsync(HttpClient client, SeriesDto series, bool receipt, decimal unitValue = 100m)
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
                lines = new[] { new { description = "Servicio de consultoría", unitCode = "ZZ", tax = new { quantity = 1m, unitValue, igvAffectationCode = "10" } } },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
    }

    private static async Task<List<SummaryDto>> CreateSummariesAsync(HttpClient client, Guid companyId)
    {
        var response = await client.PostAsJsonAsync("/api/v1/summaries", new { companyId, referenceDate = Iso(TodayInLima()) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!;
    }

    private static async Task<ElectronicDocumentDto> ActAsync(HttpClient client, Guid id, string action)
    {
        var response = await client.PostAsync($"/api/v1/electronic-documents/{id}/{action}", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
    }

    private static async Task<ElectronicDocumentDto> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/electronic-documents/{id}", ApiFixture.JsonOptions))!;

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private static string Identifier(SummaryDto summary, CompanyDto company) => summary.Document.FileBaseName[(company.Ruc.Length + 1)..];

    private async Task<(Setup Setup, List<DocumentDto> Receipts, SummaryDto Summary)> ReadyAsync(string name, int receipts = 3)
    {
        var setup = await NewTenantAsync(name);
        var list = new List<DocumentDto>();
        for (var i = 0; i < receipts; i++)
        {
            list.Add(await NewDocumentAsync(setup.Owner, setup.ReceiptSeries, receipt: true));
        }

        await NewDocumentAsync(setup.Owner, setup.InvoiceSeries, receipt: false); // invoices never go in a summary
        var summaries = await CreateSummariesAsync(setup.Owner, setup.Company.Id);
        return (setup, list, Assert.Single(summaries));
    }

    // ---------- creating ----------

    [Fact]
    public async Task A_summary_reports_the_days_receipts_signed_and_named_as_the_manual_says()
    {
        var (setup, receipts, summary) = await ReadyAsync("Rc Create SAC");
        var today = TodayInLima();

        Assert.Equal("RC", summary.Document.DocumentTypeCode);
        Assert.Equal(EDocumentState.ReadyToSend, summary.Document.State);
        Assert.Equal($"{setup.Company.Ruc}-RC-{today:yyyyMMdd}-1", summary.Document.FileBaseName);
        Assert.Equal(today, summary.ReferenceDate);
        Assert.Equal(3, summary.ElectronicDocumentIds.Count);

        var xml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{summary.Document.Id}/xml");
        var inspection = new XmlDsigSigner().Verify(xml).Value;
        Assert.True(inspection.IsValid);
        Assert.Equal(summary.Document.DigestValue, inspection.DigestValue);
        foreach (var receipt in receipts)
        {
            Assert.Contains($"<cbc:ID>{receipt.Series}-{receipt.Number}</cbc:ID>", xml, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("F001-1", xml, StringComparison.Ordinal);

        var read = (await setup.Owner.GetFromJsonAsync<SummaryDto>($"/api/v1/summaries/{summary.Document.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal(summary.ElectronicDocumentIds, read.ElectronicDocumentIds);
    }

    [Fact]
    public async Task Reported_receipts_are_not_reported_twice_and_an_empty_day_has_nothing_to_summarize()
    {
        var (setup, _, _) = await ReadyAsync("Rc Twice SAC");

        var again = await setup.Owner.PostAsJsonAsync("/api/v1/summaries", new { companyId = setup.Company.Id, referenceDate = Iso(TodayInLima()) });
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("SF-CPE-009", await ProblemCodeAsync(again));

        var emptyDay = await setup.Owner.PostAsJsonAsync("/api/v1/summaries", new { companyId = setup.Company.Id, referenceDate = Iso(TodayInLima().AddDays(-1)) });
        Assert.Equal("SF-CPE-009", await ProblemCodeAsync(emptyDay));

        var future = await setup.Owner.PostAsJsonAsync("/api/v1/summaries", new { companyId = setup.Company.Id, referenceDate = Iso(TodayInLima().AddDays(2)) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, future.StatusCode);
    }

    [Fact]
    public async Task A_summary_cannot_be_created_without_a_certificate()
    {
        var setup = await NewTenantAsync("Rc NoCert SAC", certificate: false);
        await NewDocumentAsync(setup.Owner, setup.ReceiptSeries, receipt: true);

        var response = await setup.Owner.PostAsJsonAsync("/api/v1/summaries", new { companyId = setup.Company.Id, referenceDate = Iso(TodayInLima()) });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("SF-CRT-004", await ProblemCodeAsync(response));
    }

    // ---------- sending and polling ----------

    [Fact]
    public async Task The_summary_goes_through_ticket_polling_to_accepted_and_the_receipts_follow()
    {
        var (setup, receipts, summary) = await ReadyAsync("Rc Accept SAC");
        api.Sunat.EnqueueSummary(ChannelReply.Issued("201100000011227"));

        var sent = await ActAsync(setup.Owner, summary.Document.Id, "send");

        Assert.Equal(EDocumentState.AwaitingTicket, sent.State);
        Assert.Equal("201100000011227", sent.Ticket);
        Assert.NotNull(sent.NextAttemptAt);
        var call = Assert.Single(api.Sunat.SummaryCalls);
        Assert.Equal(summary.Document.FileBaseName + ".zip", call.ZipFileName);
        Assert.Equal(setup.Company.Ruc + "MODDATOS", call.Credentials.UserName);
        Assert.Empty(api.Sunat.Calls); // sendBill is never used for a summary
        foreach (var id in summary.ElectronicDocumentIds)
        {
            Assert.Equal(EDocumentState.AwaitingTicket, (await GetAsync(setup.Owner, id)).State);
        }

        api.Sunat.EnqueueStatus(ChannelReply.Processing());
        Assert.Equal(EDocumentState.AwaitingTicket, (await ActAsync(setup.Owner, summary.Document.Id, "poll")).State);

        api.Sunat.EnqueueStatus(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, Identifier(summary, setup.Company))));
        var accepted = await ActAsync(setup.Owner, summary.Document.Id, "poll");

        Assert.Equal(EDocumentState.Accepted, accepted.State);
        Assert.Equal(0, accepted.CdrResponseCode);
        Assert.Equal(["201100000011227", "201100000011227"], api.Sunat.StatusCalls);
        foreach (var id in summary.ElectronicDocumentIds)
        {
            var receipt = await GetAsync(setup.Owner, id);
            Assert.Equal(EDocumentState.Accepted, receipt.State);
            Assert.Equal(accepted.CdrProcessId, receipt.CdrProcessId);
        }

        // Final: polling again is a no-op, and nothing is left to summarize.
        Assert.Equal(EDocumentState.Accepted, (await ActAsync(setup.Owner, summary.Document.Id, "poll")).State);
        Assert.Equal(2, api.Sunat.StatusCalls.Count);
        Assert.Equal(receipts.Count, summary.ElectronicDocumentIds.Count);
    }

    [Fact]
    public async Task A_rejected_summary_sends_the_receipts_back_to_be_reported_again()
    {
        var (setup, _, summary) = await ReadyAsync("Rc Reject SAC");
        api.Sunat.EnqueueSummary(ChannelReply.Issued("T-1"));
        await ActAsync(setup.Owner, summary.Document.Id, "send");
        api.Sunat.EnqueueStatus(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, Identifier(summary, setup.Company), "2513", "Dato no cumple con formato")));

        var rejected = await ActAsync(setup.Owner, summary.Document.Id, "poll");

        Assert.Equal(EDocumentState.Rejected, rejected.State);
        Assert.Equal(2513, rejected.CdrResponseCode);
        foreach (var id in summary.ElectronicDocumentIds)
        {
            Assert.Equal(EDocumentState.ReadyToSend, (await GetAsync(setup.Owner, id)).State); // the receipts were not judged
        }

        var second = Assert.Single(await CreateSummariesAsync(setup.Owner, setup.Company.Id));
        Assert.EndsWith("-2", second.Document.FileBaseName, StringComparison.Ordinal);
        Assert.Equal(summary.ElectronicDocumentIds, second.ElectronicDocumentIds);

        api.Sunat.EnqueueSummary(ChannelReply.Issued("T-2"));
        await ActAsync(setup.Owner, second.Document.Id, "send");
        api.Sunat.EnqueueStatus(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, Identifier(second, setup.Company))));
        Assert.Equal(EDocumentState.Accepted, (await ActAsync(setup.Owner, second.Document.Id, "poll")).State);
        foreach (var id in second.ElectronicDocumentIds)
        {
            Assert.Equal(EDocumentState.Accepted, (await GetAsync(setup.Owner, id)).State);
        }
    }

    [Fact]
    public async Task Failures_keep_the_summary_and_its_receipts_in_lockstep()
    {
        var (setup, _, summary) = await ReadyAsync("Rc Lockstep SAC");

        api.Sunat.EnqueueSummary(ChannelReply.Down("timeout"));
        var transient = await ActAsync(setup.Owner, summary.Document.Id, "send");
        Assert.Equal(EDocumentState.ReadyToSend, transient.State);
        Assert.Equal(1, transient.Attempts);
        foreach (var id in summary.ElectronicDocumentIds)
        {
            var receipt = await GetAsync(setup.Owner, id);
            Assert.Equal((EDocumentState.ReadyToSend, 1), (receipt.State, receipt.Attempts));
        }

        api.Sunat.EnqueueSummary(ChannelReply.Failed(new SunatFault(SunatSide.Client, 1033, "formato", Retryable: false)));
        var failed = await ActAsync(setup.Owner, summary.Document.Id, "send");
        Assert.Equal(EDocumentState.Failed, failed.State);
        foreach (var id in summary.ElectronicDocumentIds)
        {
            Assert.Equal(EDocumentState.Failed, (await GetAsync(setup.Owner, id)).State);
        }

        var retried = await ActAsync(setup.Owner, summary.Document.Id, "retry");
        Assert.Equal((EDocumentState.ReadyToSend, 0), (retried.State, retried.Attempts));
        foreach (var id in summary.ElectronicDocumentIds)
        {
            var receipt = await GetAsync(setup.Owner, id);
            Assert.Equal((EDocumentState.ReadyToSend, 0), (receipt.State, receipt.Attempts));
        }
    }

    [Fact]
    public async Task A_cdr_that_is_not_for_this_summary_is_never_accepted()
    {
        var (setup, _, summary) = await ReadyAsync("Rc Mismatch SAC");
        api.Sunat.EnqueueSummary(ChannelReply.Issued("T-1"));
        await ActAsync(setup.Owner, summary.Document.Id, "send");
        api.Sunat.EnqueueStatus(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, "RC-20200101-9")));

        var result = await ActAsync(setup.Owner, summary.Document.Id, "poll");

        Assert.Equal(EDocumentState.Failed, result.State);
        Assert.Equal("SF-CPE-006", result.LastErrorCode);
        Assert.Null(result.CdrResponseCode);
    }

    [Fact]
    public async Task Status_queries_that_fail_keep_waiting_and_permanent_faults_stop()
    {
        var (setup, _, summary) = await ReadyAsync("Rc PollFail SAC");
        api.Sunat.EnqueueSummary(ChannelReply.Issued("T-1"));
        await ActAsync(setup.Owner, summary.Document.Id, "send");

        api.Sunat.EnqueueStatus(ChannelReply.Down("sunat down"));
        Assert.Equal(EDocumentState.AwaitingTicket, (await ActAsync(setup.Owner, summary.Document.Id, "poll")).State);

        api.Sunat.EnqueueStatus(ChannelReply.Failed(new SunatFault(SunatSide.Client, 1001, "ticket inválido", Retryable: false)));
        Assert.Equal(EDocumentState.Failed, (await ActAsync(setup.Owner, summary.Document.Id, "poll")).State);
    }

    [Fact]
    public async Task Polling_needs_a_pending_ticket_and_single_receipts_are_still_not_sent()
    {
        var (setup, _, summary) = await ReadyAsync("Rc Guards SAC");

        var early = await setup.Owner.PostAsync($"/api/v1/electronic-documents/{summary.Document.Id}/poll", null);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("SF-CPE-004", await ProblemCodeAsync(early));

        var single = await setup.Owner.PostAsync($"/api/v1/electronic-documents/{summary.ElectronicDocumentIds[0]}/send", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, single.StatusCode);
        Assert.Empty(api.Sunat.Calls);
        Assert.Empty(api.Sunat.SummaryCalls);
    }

    // ---------- security ----------

    [Fact]
    public async Task Summaries_are_isolated_between_tenants_and_guarded_by_role()
    {
        var (a, _, summary) = await ReadyAsync("Rc Iso A SAC");
        var b = await NewTenantAsync("Rc Iso B SAC");

        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/summaries/{summary.Document.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PostAsync($"/api/v1/electronic-documents/{summary.Document.Id}/send", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PostAsync($"/api/v1/electronic-documents/{summary.Document.Id}/poll", null)).StatusCode);
        var foreignCompany = await b.Owner.PostAsJsonAsync("/api/v1/summaries", new { companyId = a.Company.Id, referenceDate = Iso(TodayInLima()) });
        Assert.Equal(HttpStatusCode.NotFound, foreignCompany.StatusCode);
        Assert.Equal("SF-ORG-002", await ProblemCodeAsync(foreignCompany));

        var sales = await ApiFixture.CreateUserAsync(a.Owner, Roles.Sales, a.TenantId);
        using var salesClient = api.ClientFor(await api.LoginOkAsync(sales.Email, sales.Password));
        Assert.Equal(HttpStatusCode.OK, (await salesClient.GetAsync($"/api/v1/summaries/{summary.Document.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await salesClient.PostAsJsonAsync("/api/v1/summaries", new { companyId = a.Company.Id, referenceDate = Iso(TodayInLima()) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await salesClient.PostAsync($"/api/v1/electronic-documents/{summary.Document.Id}/send", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await salesClient.PostAsync($"/api/v1/electronic-documents/{summary.Document.Id}/poll", null)).StatusCode);
        Assert.Empty(api.Sunat.SummaryCalls);
    }

    [Fact]
    public async Task The_database_protects_summary_items_and_finished_summaries()
    {
        var (setup, _, summary) = await ReadyAsync("Rc Triggers SAC");
        api.Sunat.EnqueueSummary(ChannelReply.Issued("T-1"));
        await ActAsync(setup.Owner, summary.Document.Id, "send");
        api.Sunat.EnqueueStatus(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, Identifier(summary, setup.Company))));
        await ActAsync(setup.Owner, summary.Document.Id, "poll");

        await using var connection = new NpgsqlConnection(api.Postgres.OwnerConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[]
        {
            $"UPDATE cpe.summary_item SET line_number = 99 WHERE summary_id = '{summary.Document.Id}'",
            $"UPDATE cpe.summary_item SET electronic_document_id = gen_random_uuid() WHERE summary_id = '{summary.Document.Id}'",
            $"DELETE FROM cpe.summary_item WHERE summary_id = '{summary.Document.Id}'",
            $"UPDATE cpe.electronic_document SET state = 'Rejected' WHERE id = '{summary.Document.Id}'",
            $"UPDATE cpe.electronic_document SET issue_date = '2000-01-01' WHERE id = '{summary.ElectronicDocumentIds[0]}'",
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal("42501", failure.SqlState);
        }
    }

    [Fact]
    public async Task Two_simultaneous_requests_report_each_receipt_once()
    {
        var setup = await NewTenantAsync("Rc Race SAC");
        for (var i = 0; i < 3; i++)
        {
            await NewDocumentAsync(setup.Owner, setup.ReceiptSeries, receipt: true);
        }

        var body = new { companyId = setup.Company.Id, referenceDate = Iso(TodayInLima()) };
        var responses = await Task.WhenAll(setup.Owner.PostAsJsonAsync("/api/v1/summaries", body), setup.Owner.PostAsJsonAsync("/api/v1/summaries", body));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.NotFound or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        var created = (await responses.Single(r => r.StatusCode == HttpStatusCode.Created).Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!;
        Assert.Equal(3, Assert.Single(created).ElectronicDocumentIds.Count);

        await using var connection = new NpgsqlConnection(api.Postgres.OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM cpe.summary_item i JOIN cpe.electronic_document d ON d.id = i.summary_id WHERE d.company_id = '{setup.Company.Id}' AND i.released_at IS NULL", connection);
        Assert.Equal(3L, (long)(await command.ExecuteScalarAsync())!);
    }
}
