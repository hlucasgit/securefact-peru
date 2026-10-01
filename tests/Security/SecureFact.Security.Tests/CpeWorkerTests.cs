using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine.Application;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;
using SecureFact.Workers;

namespace SecureFact.Security.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class CpeWorkerTests(ApiFixture api)
{
    private static int _rucCounter = 13_000_000;

    private sealed record Setup(Guid TenantId, HttpClient Owner, CompanyDto Company, SeriesDto InvoiceSeries, SeriesDto ReceiptSeries);

    /// <summary>The processor under test sees time shifted by an offset, so backoff and ticket intervals can be crossed without waiting.</summary>
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

    private static async Task<CompanyDto> NewCompanyAsync(HttpClient owner, bool solCredentials)
    {
        var ruc = NewRuc();
        var company = (await (await owner.PostAsJsonAsync("/api/v1/companies", new { ruc, details = new { legalName = "Emisora SAC", fiscalAddress = "Av. Larco 123", ubigeo = "150122" } }))
            .Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/certificates", new { companyId = company.Id, pfxBase64 = PfxFor(ruc), password = "pw" })).StatusCode);
        if (solCredentials)
        {
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = "Sol-Clave-worker-1" })).StatusCode);
        }

        return company;
    }

    private async Task<Setup> NewTenantAsync(string name)
    {
        api.Sunat.Reset();
        var tenantId = await api.CreateTenantAsync(name);
        using var admin = await api.AdminClientAsync();
        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        var owner = api.ClientFor(await api.LoginOkAsync(user.Email, user.Password));
        var company = await NewCompanyAsync(owner, solCredentials: true);
        return new Setup(tenantId, owner, company, await SeriesAsync(owner, company, "01", "F001"), await SeriesAsync(owner, company, "03", "B001"));
    }

    private static async Task<SeriesDto> SeriesAsync(HttpClient owner, CompanyDto company, string type, string code) =>
        (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = type, code })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

    private static async Task<DocumentDto> NewDocumentAsync(HttpClient client, SeriesDto series, bool receipt, DateOnly? issueDate = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
        {
            Content = JsonContent.Create(new
            {
                seriesId = series.Id,
                issueDate = Iso(issueDate ?? TodayInLima()),
                currency = "PEN",
                buyer = receipt
                    ? new { documentTypeCode = "1", documentNumber = "12345678", name = "Persona Natural" }
                    : new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
                lines = new[] { new { description = "Servicio de consultoría", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10" } } },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
    }

    private static async Task<ElectronicDocumentDto> PrepareAsync(HttpClient client, Guid documentId)
    {
        var response = await client.PostAsync($"/api/v1/documents/{documentId}/electronic", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
    }

    private static async Task<ElectronicDocumentDto> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/electronic-documents/{id}", ApiFixture.JsonOptions))!;

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.Postgres.OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    // ---------- invoices ----------

    [Fact]
    public async Task The_worker_sends_a_due_invoice_and_waits_out_the_backoff_after_a_failure()
    {
        var setup = await NewTenantAsync("Worker Send SAC");
        var document = await NewDocumentAsync(setup.Owner, setup.InvoiceSeries, receipt: false);
        var electronic = await PrepareAsync(setup.Owner, document.Id);

        api.Sunat.Enqueue(ChannelReply.Down("timeout"));
        var first = await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId);
        Assert.Equal(1, first.Sent);
        var waiting = await GetAsync(setup.Owner, electronic.Id);
        Assert.Equal((EDocumentState.ReadyToSend, 1), (waiting.State, waiting.Attempts));
        Assert.True(waiting.NextAttemptAt > DateTimeOffset.UtcNow);

        var tooEarly = await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId);
        Assert.Equal(0, tooEarly.Sent);
        Assert.Single(api.Sunat.Calls);

        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, $"{document.Series}-{document.Number}")));
        var later = await Processor(TimeSpan.FromMinutes(2)).RunOnceAsync(CancellationToken.None, setup.TenantId);

        Assert.Equal(1, later.Sent);
        Assert.Equal(EDocumentState.Accepted, (await GetAsync(setup.Owner, electronic.Id)).State);
        Assert.Equal(2, api.Sunat.Calls.Count);

        var idle = await Processor(TimeSpan.FromMinutes(5)).RunOnceAsync(CancellationToken.None, setup.TenantId);
        Assert.Equal(WorkReport.Empty, idle);
    }

    [Fact]
    public async Task One_item_that_cannot_be_sent_does_not_stop_the_others()
    {
        var setup = await NewTenantAsync("Worker Isolation SAC");
        var noCredentials = await NewCompanyAsync(setup.Owner, solCredentials: false);
        var otherSeries = await SeriesAsync(setup.Owner, noCredentials, "01", "F001");
        var blocked = await PrepareAsync(setup.Owner, (await NewDocumentAsync(setup.Owner, otherSeries, receipt: false)).Id);
        var document = await NewDocumentAsync(setup.Owner, setup.InvoiceSeries, receipt: false);
        var healthy = await PrepareAsync(setup.Owner, document.Id);
        api.Sunat.RespondToBills(call => call.ZipFileName.StartsWith(setup.Company.Ruc, StringComparison.Ordinal)
            ? ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, $"{document.Series}-{document.Number}"))
            : ChannelReply.Down("unexpected"));

        var report = await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId);

        Assert.Equal(1, report.Sent);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(EDocumentState.Accepted, (await GetAsync(setup.Owner, healthy.Id)).State);
        var untouched = await GetAsync(setup.Owner, blocked.Id);
        Assert.Equal((EDocumentState.ReadyToSend, 0), (untouched.State, untouched.Attempts)); // a missing credential never burns an attempt
    }

    // ---------- daily summaries ----------

    [Fact]
    public async Task The_worker_summarizes_closed_days_only_sends_the_summary_and_follows_its_ticket()
    {
        var setup = await NewTenantAsync("Worker Summary SAC");
        var yesterday = TodayInLima().AddDays(-1);
        var closedA = await PrepareAsync(setup.Owner, (await NewDocumentAsync(setup.Owner, setup.ReceiptSeries, receipt: true, yesterday)).Id);
        var closedB = await PrepareAsync(setup.Owner, (await NewDocumentAsync(setup.Owner, setup.ReceiptSeries, receipt: true, yesterday)).Id);
        var openToday = await PrepareAsync(setup.Owner, (await NewDocumentAsync(setup.Owner, setup.ReceiptSeries, receipt: true)).Id);
        api.Sunat.EnqueueSummary(ChannelReply.Issued("T-100"));

        var first = await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId);

        Assert.Equal(1, first.SummariesCreated);
        Assert.Equal(1, first.Sent);
        var summaryCall = Assert.Single(api.Sunat.SummaryCalls);
        Assert.Contains($"-RC-{TodayInLima():yyyyMMdd}-1.zip", summaryCall.ZipFileName, StringComparison.Ordinal);
        Assert.Equal(EDocumentState.AwaitingTicket, (await GetAsync(setup.Owner, closedA.Id)).State);
        Assert.Equal(EDocumentState.AwaitingTicket, (await GetAsync(setup.Owner, closedB.Id)).State);
        Assert.Equal(EDocumentState.ReadyToSend, (await GetAsync(setup.Owner, openToday.Id)).State); // today is still open

        // The next ticket query is not due yet.
        Assert.Equal(0, (await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId)).Polled);
        Assert.Empty(api.Sunat.StatusCalls);

        var summaryName = summaryCall.ZipFileName[..^".zip".Length];
        api.Sunat.EnqueueStatus(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, summaryName[(setup.Company.Ruc.Length + 1)..])));
        var second = await Processor(TimeSpan.FromMinutes(2)).RunOnceAsync(CancellationToken.None, setup.TenantId);

        Assert.Equal(1, second.Polled);
        Assert.Equal(["T-100"], api.Sunat.StatusCalls);
        Assert.Equal(EDocumentState.Accepted, (await GetAsync(setup.Owner, closedA.Id)).State);
        Assert.Equal(EDocumentState.Accepted, (await GetAsync(setup.Owner, closedB.Id)).State);
        Assert.Equal(EDocumentState.ReadyToSend, (await GetAsync(setup.Owner, openToday.Id)).State);

        // Reported receipts are never summarized again.
        var third = await Processor(TimeSpan.FromMinutes(5)).RunOnceAsync(CancellationToken.None, setup.TenantId);
        Assert.Equal(0, third.SummariesCreated);
        Assert.Single(api.Sunat.SummaryCalls);
    }

    // ---------- stuck documents ----------

    [Fact]
    public async Task A_document_stuck_in_sending_is_reported_never_resent_and_only_an_operator_can_recover_it()
    {
        var setup = await NewTenantAsync("Worker Stuck SAC");
        var document = await NewDocumentAsync(setup.Owner, setup.InvoiceSeries, receipt: false);
        var electronic = await PrepareAsync(setup.Owner, document.Id);
        await ExecuteAsync($"UPDATE cpe.electronic_document SET state = 'Sending', attempts = 1, updated_at = now() - interval '20 minutes' WHERE id = '{electronic.Id}'");

        var report = await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId);

        Assert.Equal(1, report.Stuck);
        Assert.Equal(0, report.Sent);
        Assert.Empty(api.Sunat.Calls);
        Assert.Equal(EDocumentState.Sending, (await GetAsync(setup.Owner, electronic.Id)).State);

        var recovered = await setup.Owner.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/recover", null);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(EDocumentState.ReadyToSend, (await recovered.Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!.State);

        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, $"{document.Series}-{document.Number}")));
        Assert.Equal(1, (await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId)).Sent);
    }

    [Fact]
    public async Task Recovering_needs_a_document_that_is_really_stuck_and_the_right_role()
    {
        var setup = await NewTenantAsync("Worker Recover SAC");
        var fresh = await PrepareAsync(setup.Owner, (await NewDocumentAsync(setup.Owner, setup.InvoiceSeries, receipt: false)).Id);

        var notSending = await setup.Owner.PostAsync($"/api/v1/electronic-documents/{fresh.Id}/recover", null);
        Assert.Equal(HttpStatusCode.Conflict, notSending.StatusCode);

        await ExecuteAsync($"UPDATE cpe.electronic_document SET state = 'Sending', attempts = 1, updated_at = now() WHERE id = '{fresh.Id}'");
        var tooSoon = await setup.Owner.PostAsync($"/api/v1/electronic-documents/{fresh.Id}/recover", null);
        Assert.Equal(HttpStatusCode.Conflict, tooSoon.StatusCode);
        using var body = JsonDocument.Parse(await tooSoon.Content.ReadAsStringAsync());
        Assert.Equal("SF-CPE-008", body.RootElement.GetProperty("code").GetString());

        var sales = await ApiFixture.CreateUserAsync(setup.Owner, Roles.Sales, setup.TenantId);
        using var salesClient = api.ClientFor(await api.LoginOkAsync(sales.Email, sales.Password));
        Assert.Equal(HttpStatusCode.Forbidden, (await salesClient.PostAsync($"/api/v1/electronic-documents/{fresh.Id}/recover", null)).StatusCode);
        Assert.Equal(EDocumentState.Sending, (await GetAsync(setup.Owner, fresh.Id)).State);
    }

    // ---------- the real host ----------

    [Fact]
    public async Task The_hosted_worker_runs_with_the_real_composition_and_processes_documents_on_its_own()
    {
        var setup = await NewTenantAsync("Worker Host SAC");
        var document = await NewDocumentAsync(setup.Owner, setup.InvoiceSeries, receipt: false);
        var electronic = await PrepareAsync(setup.Owner, document.Id);
        api.Sunat.RespondToBills(call => call.ZipFileName.StartsWith(setup.Company.Ruc, StringComparison.Ordinal)
            ? ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, $"{document.Series}-{document.Number}"))
            : ChannelReply.Down("not mine"));

        var appConnection = new NpgsqlConnectionStringBuilder(api.Postgres.AppConnectionString) { MaxPoolSize = 10 }.ConnectionString;
        var builder = WorkerHost.Create([$"--ConnectionStrings:App={appConnection}", "--Cpe:WorkerIntervalSeconds=1", "--environment=Development"]);
        builder.Services.AddSingleton<ICpeSubmissionChannel>(api.Sunat);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
        using var host = builder.Build();

        await host.StartAsync();
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
            ElectronicDocumentDto current;
            do
            {
                await Task.Delay(500);
                current = await GetAsync(setup.Owner, electronic.Id);
            }
            while (current.State != EDocumentState.Accepted && DateTimeOffset.UtcNow < deadline);

            Assert.Equal(EDocumentState.Accepted, current.State);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
