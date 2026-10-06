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
using SecureFact.CpeEngine.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Messaging.RabbitMq;
using SecureFact.Organizations.Contracts;
using SecureFact.Platform.Messaging;
using SecureFact.SharedKernel.Messaging;
using SecureFact.Workers;

namespace SecureFact.Security.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class OutboxTests(ApiFixture api, RabbitFixture rabbit)
{
    private static int _rucCounter = 15_000_000;

    private sealed record Setup(Guid TenantId, HttpClient Owner, CompanyDto Company, SeriesDto InvoiceSeries, string Ruc);

    private OutboxProcessor Processor() => new(api.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OutboxProcessor>.Instance);

    private static string NewRuc()
    {
        var body = "20" + Interlocked.Increment(ref _rucCounter).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var sum = body.Select((c, i) => (c - '0') * weights[i]).Sum();
        return body + ((11 - (sum % 11)) % 10);
    }

    private static DateOnly TodayInLima() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Lima")).DateTime);

    private static string PfxFor(string ruc)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=Representante Demo, OU={ruc}, O=EMISORA SAC, C=PE", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, "pw"));
    }

    private static Task<HttpResponseMessage> UploadCertificateAsync(Setup setup) =>
        setup.Owner.PostAsJsonAsync("/api/v1/certificates", new { companyId = setup.Company.Id, pfxBase64 = PfxFor(setup.Ruc), password = "pw" });

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
        var series = (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = "01", code = "F001" })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;
        var setup = new Setup(tenantId, owner, company, series, ruc);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = "Sol-Clave-outbox-1" })).StatusCode);
        if (certificate)
        {
            Assert.Equal(HttpStatusCode.Created, (await UploadCertificateAsync(setup)).StatusCode);
        }

        return setup;
    }

    private static async Task<HttpResponseMessage> IssueAsync(Setup setup, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
        {
            Content = JsonContent.Create(new
            {
                seriesId = setup.InvoiceSeries.Id,
                issueDate = TodayInLima().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                currency = "PEN",
                buyer = new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
                lines = new[] { new { description = "Servicio de consultoría", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10" } } },
            }),
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await setup.Owner.SendAsync(request);
    }

    private static async Task<DocumentDto> IssueOkAsync(Setup setup)
    {
        var response = await IssueAsync(setup);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(api.Postgres.OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.Postgres.OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private Task<long> CountAsync(Guid tenantId, string where = "TRUE") =>
        ScalarAsync<long>($"SELECT count(*) FROM billing.outbox_message WHERE tenant_id = '{tenantId}' AND {where}");

    private static async Task<HttpResponseMessage> ElectronicAsync(Setup setup, Guid documentId) =>
        await setup.Owner.GetAsync($"/api/v1/documents/{documentId}/electronic");

    // ---------- writing ----------

    [Fact]
    public async Task Issuing_a_document_writes_its_event_in_the_same_transaction_and_a_replay_adds_none()
    {
        var setup = await NewTenantAsync("Outbox Write SAC");
        var key = Guid.NewGuid().ToString("N");

        var first = await IssueAsync(setup, key);
        var replay = await IssueAsync(setup, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var document = (await first.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(document.Id, (await replay.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!.Id);
        Assert.Equal(1, await CountAsync(setup.TenantId));
        var payload = await ScalarAsync<string>($"SELECT payload::text FROM billing.outbox_message WHERE tenant_id = '{setup.TenantId}'");
        using var json = JsonDocument.Parse(payload);
        Assert.Equal(document.Id, json.RootElement.GetProperty("documentId").GetGuid());
        Assert.Equal("01", json.RootElement.GetProperty("documentTypeCode").GetString());
        Assert.Equal("billing.document.issued", await ScalarAsync<string>($"SELECT event_type FROM billing.outbox_message WHERE tenant_id = '{setup.TenantId}'"));
    }

    [Fact]
    public async Task A_rejected_issue_leaves_no_event_behind()
    {
        var setup = await NewTenantAsync("Outbox Rollback SAC");
        await ExecuteAsync($"UPDATE billing.series SET last_number = 99999999 WHERE id = '{setup.InvoiceSeries.Id}'");

        var response = await IssueAsync(setup);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await CountAsync(setup.TenantId));
    }

    // ---------- delivery ----------

    [Fact]
    public async Task The_dispatcher_prepares_the_electronic_document_on_its_own_and_delivers_each_event_once()
    {
        var setup = await NewTenantAsync("Outbox Deliver SAC");
        var document = await IssueOkAsync(setup);
        Assert.Equal(HttpStatusCode.NotFound, (await ElectronicAsync(setup, document.Id)).StatusCode);

        // Two events travel: the one of Billing, and the one of the CPE engine that its consumer queues to archive the signed XML (ADR-036). Which pass carries the second depends on the order of the sources.
        var first = await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId);
        var second = await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId);

        Assert.Equal(new OutboxReport(2, 0, 0), new OutboxReport(first.Delivered + second.Delivered, first.Failed + second.Failed, first.Dead + second.Dead));
        var electronic = (await (await ElectronicAsync(setup, document.Id)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(EDocumentState.ReadyToSend, electronic.State);
        Assert.Equal(1, await CountAsync(setup.TenantId, "processed_at IS NOT NULL"));

        Assert.Equal(OutboxReport.Empty, await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId));
    }

    [Fact]
    public async Task A_failing_consumer_is_retried_with_backoff_until_the_cause_is_fixed()
    {
        var setup = await NewTenantAsync("Outbox Retry SAC", certificate: false);
        var document = await IssueOkAsync(setup);

        var failed = await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId);

        Assert.Equal(new OutboxReport(0, 1, 0), failed);
        Assert.Equal(1, await ScalarAsync<int>($"SELECT attempts FROM billing.outbox_message WHERE tenant_id = '{setup.TenantId}'"));
        Assert.Contains("SF-CRT-004", await ScalarAsync<string>($"SELECT last_error FROM billing.outbox_message WHERE tenant_id = '{setup.TenantId}'"), StringComparison.Ordinal);
        Assert.Equal(1, await CountAsync(setup.TenantId, "next_attempt_at > now() AND locked_until IS NULL"));

        Assert.Equal(HttpStatusCode.Created, (await UploadCertificateAsync(setup)).StatusCode);
        Assert.Equal(OutboxReport.Empty, await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId)); // still waiting out the backoff

        await ExecuteAsync($"UPDATE billing.outbox_message SET next_attempt_at = now() - interval '5 seconds' WHERE tenant_id = '{setup.TenantId}'");
        var delivered = await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId);

        Assert.Equal(new OutboxReport(1, 0, 0), delivered);
        Assert.Equal(HttpStatusCode.OK, (await ElectronicAsync(setup, document.Id)).StatusCode);
    }

    [Fact]
    public async Task A_message_that_exhausts_its_attempts_dies_and_an_operator_can_requeue_it()
    {
        var setup = await NewTenantAsync("Outbox Dead SAC", certificate: false);
        var document = await IssueOkAsync(setup);
        await ExecuteAsync($"UPDATE billing.outbox_message SET attempts = {OutboxPolicy.MaxAttempts - 1} WHERE tenant_id = '{setup.TenantId}'");

        var report = await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId);

        Assert.Equal(new OutboxReport(0, 1, 1), report);
        Assert.Equal(1, await CountAsync(setup.TenantId, "dead_at IS NOT NULL"));
        await ExecuteAsync($"UPDATE billing.outbox_message SET next_attempt_at = now() - interval '5 seconds' WHERE tenant_id = '{setup.TenantId}'");
        Assert.Equal(OutboxReport.Empty, await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId)); // dead messages are never claimed

        var dead = (await setup.Owner.GetFromJsonAsync<List<DeadOutboxMessage>>("/api/v1/outbox/dead", ApiFixture.JsonOptions))!;
        var message = Assert.Single(dead);
        Assert.Equal("billing", message.Source);
        Assert.Equal(OutboxPolicy.MaxAttempts, message.Attempts);
        Assert.Contains("SF-CRT-004", message.LastError, StringComparison.Ordinal);

        // Another tenant sees nothing and cannot requeue it.
        var other = await NewTenantAsync("Outbox Dead Other SAC");
        Assert.Empty((await other.Owner.GetFromJsonAsync<List<DeadOutboxMessage>>("/api/v1/outbox/dead", ApiFixture.JsonOptions))!);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.PostAsync($"/api/v1/outbox/billing/{message.Id}/requeue", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await UploadCertificateAsync(setup)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Owner.PostAsync($"/api/v1/outbox/billing/{message.Id}/requeue", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Owner.PostAsync($"/api/v1/outbox/billing/{message.Id}/requeue", null)).StatusCode); // no longer dead

        Assert.Equal(new OutboxReport(1, 0, 0), await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId));
        Assert.Equal(HttpStatusCode.OK, (await ElectronicAsync(setup, document.Id)).StatusCode);
    }

    [Fact]
    public async Task An_event_nobody_consumes_fails_visibly_instead_of_disappearing()
    {
        var setup = await NewTenantAsync("Outbox Unknown SAC");
        await ExecuteAsync(
            "INSERT INTO billing.outbox_message (id, tenant_id, event_type, payload, created_at, attempts, next_attempt_at) " +
            $"VALUES (gen_random_uuid(), '{setup.TenantId}', 'billing.unknown.event', '" + "{}" + "', now(), 0, now() - interval '5 seconds')");

        var report = await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId);

        Assert.Equal(new OutboxReport(0, 1, 0), report);
        Assert.Contains("No handler", await ScalarAsync<string>($"SELECT last_error FROM billing.outbox_message WHERE tenant_id = '{setup.TenantId}'"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_leased_message_is_not_claimed_and_parallel_dispatchers_deliver_each_event_once()
    {
        var setup = await NewTenantAsync("Outbox Parallel SAC");
        var documents = new List<DocumentDto>();
        for (var i = 0; i < 6; i++)
        {
            documents.Add(await IssueOkAsync(setup));
        }

        await ExecuteAsync($"UPDATE billing.outbox_message SET locked_until = now() + interval '5 minutes' WHERE tenant_id = '{setup.TenantId}' AND created_at = (SELECT min(created_at) FROM billing.outbox_message WHERE tenant_id = '{setup.TenantId}')");
        var reports = await Task.WhenAll(Processor().RunOnceAsync(CancellationToken.None, setup.TenantId), Processor().RunOnceAsync(CancellationToken.None, setup.TenantId));

        Assert.True(reports.Sum(r => r.Delivered) >= 5); // one is leased by someone else; the other five are shared out without overlap (the archive events of the CPE engine count too)
        Assert.Equal(0, reports.Sum(r => r.Failed));
        Assert.Equal(5L, await CountAsync(setup.TenantId, "processed_at IS NOT NULL"));
        Assert.Equal(1, await CountAsync(setup.TenantId, "processed_at IS NULL"));

        // When the lease expires the message is delivered by the next pass (a crashed dispatcher does not lose it).
        await ExecuteAsync($"UPDATE billing.outbox_message SET locked_until = now() - interval '1 second' WHERE tenant_id = '{setup.TenantId}' AND processed_at IS NULL");
        Assert.True((await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId)).Delivered >= 1);
        Assert.Equal(0, await CountAsync(setup.TenantId, "processed_at IS NULL"));
        foreach (var document in documents)
        {
            Assert.Equal(HttpStatusCode.OK, (await ElectronicAsync(setup, document.Id)).StatusCode);
        }
    }

    // ---------- security ----------

    [Fact]
    public async Task The_events_are_immutable_and_cannot_be_deleted_even_by_the_database_owner()
    {
        var setup = await NewTenantAsync("Outbox Immutable SAC");
        await IssueOkAsync(setup);

        foreach (var sql in new[]
        {
            $"UPDATE billing.outbox_message SET payload = '[]' WHERE tenant_id = '{setup.TenantId}'",
            $"UPDATE billing.outbox_message SET event_type = 'billing.other' WHERE tenant_id = '{setup.TenantId}'",
            $"UPDATE billing.outbox_message SET tenant_id = gen_random_uuid() WHERE tenant_id = '{setup.TenantId}'",
            $"DELETE FROM billing.outbox_message WHERE tenant_id = '{setup.TenantId}'",
        })
        {
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql));
        }

        await using var app = new NpgsqlConnection(api.Postgres.AppConnectionString);
        await app.OpenAsync();
        await using var delete = new NpgsqlCommand("DELETE FROM billing.outbox_message", app);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task Only_operators_can_see_and_requeue_dead_messages()
    {
        var setup = await NewTenantAsync("Outbox Rbac SAC");
        var sales = await ApiFixture.CreateUserAsync(setup.Owner, Roles.Sales, setup.TenantId);
        using var salesClient = api.ClientFor(await api.LoginOkAsync(sales.Email, sales.Password));
        using var anonymous = api.NewClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await salesClient.GetAsync("/api/v1/outbox/dead")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await salesClient.PostAsync($"/api/v1/outbox/billing/{Guid.NewGuid()}/requeue", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/outbox/dead")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await setup.Owner.GetAsync("/api/v1/outbox/dead")).StatusCode);
    }

    // ---------- the whole chain, hosted ----------

    [Fact]
    public async Task An_issued_invoice_reaches_accepted_with_no_manual_step_when_the_worker_runs()
    {
        var setup = await NewTenantAsync("Outbox Chain SAC");
        await ExecuteAsync("UPDATE billing.outbox_message SET next_attempt_at = now() + interval '1 day' WHERE processed_at IS NULL AND dead_at IS NULL"); // leave other tests' leftovers alone
        var document = await IssueOkAsync(setup);
        api.Sunat.RespondToBills(call => call.ZipFileName.StartsWith(setup.Ruc, StringComparison.Ordinal)
            ? ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Ruc, $"{document.Series}-{document.Number}"))
            : ChannelReply.Down("not mine"));

        var appConnection = new NpgsqlConnectionStringBuilder(api.Postgres.AppConnectionString) { MaxPoolSize = 10 }.ConnectionString;
        var builder = WorkerHost.Create([$"--ConnectionStrings:App={appConnection}", "--Cpe:WorkerIntervalSeconds=1", "--Outbox:WorkerIntervalSeconds=1", "--environment=Development"]);
        builder.Services.AddSingleton<ICpeSubmissionChannel>(api.Sunat);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
        using var host = builder.Build();

        await host.StartAsync();
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
            ElectronicDocumentDto? current = null;
            do
            {
                await Task.Delay(500);
                var response = await ElectronicAsync(setup, document.Id);
                current = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions) : null;
            }
            while (current?.State != EDocumentState.Accepted && DateTimeOffset.UtcNow < deadline);

            Assert.Equal(EDocumentState.Accepted, current?.State);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------- retention ----------

    [Fact]
    public async Task Delivered_messages_are_purged_after_the_retention_and_the_table_stays_append_only_for_everything_else()
    {
        var setup = await NewTenantAsync("Outbox Purge SAC");
        for (var n = 0; n < 4; n++)
        {
            await IssueOkAsync(setup);
        }

        Assert.Equal(new OutboxReport(4, 0, 0), await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId));
        var ids = await ScalarAsync<Guid[]>($"SELECT array_agg(id ORDER BY created_at) FROM billing.outbox_message WHERE tenant_id = '{setup.TenantId}'");
        var (delivered, recent, pending, dead) = (ids[0], ids[1], ids[2], ids[3]);

        await ExecuteAsync($"UPDATE billing.outbox_message SET processed_at = now() - interval '40 days' WHERE id = '{delivered}'");
        await ExecuteAsync($"UPDATE billing.outbox_message SET processed_at = now() - interval '2 days' WHERE id = '{recent}'");
        await ExecuteAsync($"UPDATE billing.outbox_message SET processed_at = NULL WHERE id = '{pending}'");
        await ExecuteAsync($"UPDATE billing.outbox_message SET processed_at = NULL, dead_at = now() - interval '50 days', attempts = {OutboxPolicy.MaxAttempts} WHERE id = '{dead}'");

        var removed = await Processor().PurgeAsync(TimeSpan.FromDays(30), CancellationToken.None);

        Assert.True(removed >= 1);
        Assert.Equal(0, await CountAsync(setup.TenantId, $"id = '{delivered}'"));
        Assert.Equal(1, await CountAsync(setup.TenantId, $"id = '{recent}'")); // delivered but still inside the retention
        Assert.Equal(1, await CountAsync(setup.TenantId, $"id = '{pending}'")); // never removed while pending
        Assert.Equal(1, await CountAsync(setup.TenantId, $"id = '{dead}'")); // nor while it waits for an operator

        // Less than a day of retention is refused by the function itself.
        var tooShort = await Assert.ThrowsAsync<PostgresException>(() => Processor().PurgeAsync(TimeSpan.FromHours(12), CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.InvalidParameterValue, tooShort.SqlState);

        // Outside the function nobody can delete a message: not the schema owner, and not the runtime role even if it sets the marker the function sets.
        var owner = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"DELETE FROM billing.outbox_message WHERE id = '{recent}'"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, owner.SqlState);
        await using var connection = new NpgsqlConnection(api.Postgres.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT set_config('app.scope', 'platform', false), set_config('app.outbox_purge', 'on', false); DELETE FROM billing.outbox_message WHERE id = '{recent}'", connection);
        var app = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, app.SqlState);
        Assert.Equal(1, await CountAsync(setup.TenantId, $"id = '{recent}'"));
    }

    // ---------- the message bus ----------

    [Fact]
    public async Task With_a_broker_configured_every_event_also_reaches_the_bus_after_its_own_consumer_ran()
    {
        var setup = await NewTenantAsync("Outbox Bus SAC");
        var exchange = $"test.events.{Guid.NewGuid():N}";
        await using var subscription = await rabbit.SubscribeAsync(exchange, "billing.#");
        var document = await IssueOkAsync(setup);
        var messageId = await ScalarAsync<Guid>($"SELECT id FROM billing.outbox_message WHERE tenant_id = '{setup.TenantId}'");

        var appConnection = new NpgsqlConnectionStringBuilder(api.Postgres.AppConnectionString) { MaxPoolSize = 10 }.ConnectionString;
        var builder = WorkerHost.Create(
        [
            $"--ConnectionStrings:App={appConnection}", "--Cpe:WorkerIntervalSeconds=1", "--Outbox:WorkerIntervalSeconds=1", "--environment=Development",
            $"--RabbitMq:Host={rabbit.Host}", $"--RabbitMq:Port={rabbit.Port}", $"--RabbitMq:UserName={RabbitFixture.User}", $"--RabbitMq:Password={RabbitFixture.Password}", $"--RabbitMq:Exchange={exchange}",
        ]);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
        using var host = builder.Build();

        await host.StartAsync();
        try
        {
            // The worker drains the outbox of every tenant: look for the message of this document among whatever else it publishes.
            RabbitMQ.Client.BasicGetResult? ours = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (ours is null && DateTimeOffset.UtcNow < deadline)
            {
                var next = await subscription.NextAsync(TimeSpan.FromSeconds(5));
                if (next?.BasicProperties.MessageId == messageId.ToString("D"))
                {
                    ours = next;
                }
            }

            Assert.NotNull(ours);
            Assert.Equal("billing.document.issued", ours.RoutingKey);
            Assert.Equal(setup.TenantId.ToString("D"), System.Text.Encoding.UTF8.GetString((byte[])ours.BasicProperties.Headers![RabbitMqMessageBus.TenantHeader]!));
            using var payload = JsonDocument.Parse(ours.Body.ToArray());
            Assert.Equal(document.Id, payload.RootElement.GetProperty("documentId").GetGuid());

            // Its own consumer ran too, and the message is complete.
            Assert.Equal(HttpStatusCode.OK, (await ElectronicAsync(setup, document.Id)).StatusCode);
            Assert.Equal(1, await CountAsync(setup.TenantId, "processed_at IS NOT NULL"));
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
