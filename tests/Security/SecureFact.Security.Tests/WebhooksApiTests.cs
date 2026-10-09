// The bodies of the requests repeat the same small lists of events in every test: naming each as a field would hide what the test sends.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;
using SecureFact.Platform.Messaging;
using SecureFact.Webhooks.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>Webhooks (ADR-067): the tenant registers an address and the events it wants; each event arrives signed and is tried again until the address answers.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class WebhooksApiTests(ApiFixture api)
{
    private const string SolPassword = "Sol-Clave-webhooks-secret-7";
    private static int _rucCounter = 19_000_000;

    private sealed record Hook(Guid Id, string Url, string? Description, List<string> Events, bool IsActive, string SecretHint, int ConsecutiveFailures, DateTimeOffset? DisabledAt, string? DisabledReason);

    private sealed record Created(Hook Endpoint, string Secret);

    private sealed record Delivery(Guid Id, Guid EndpointId, Guid EventId, string EventType, string State, int Attempts, DateTimeOffset? NextAttemptAt, int? LastStatusCode, string? LastError, DateTimeOffset? DeliveredAt);

    private sealed record Setup(Guid TenantId, HttpClient Owner, CompanyDto Company, SeriesDto InvoiceSeries);

    private static string NewRuc()
    {
        var body = "20" + Interlocked.Increment(ref _rucCounter).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var sum = body.Select((c, i) => (c - '0') * weights[i]).Sum();
        return body + ((11 - (sum % 11)) % 10);
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private static string PfxFor(string ruc)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=Representante Demo, OU={ruc}, O=EMISORA SAC, C=PE", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, "pw"));
    }

    private static DateOnly TodayInLima() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Lima")).DateTime);

    private async Task<(Guid TenantId, HttpClient Owner)> TenantAsync(string name)
    {
        using var admin = await api.AdminClientAsync();
        var tenantId = await api.CreateTenantAsync(name);
        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        return (tenantId, api.ClientFor(await api.LoginOkAsync(user.Email, user.Password)));
    }

    /// <summary>A tenant that can issue and send: company, certificate, SOL credentials and an invoice series.</summary>
    private async Task<Setup> IssuerAsync(string name)
    {
        var (tenantId, owner) = await TenantAsync(name);
        var ruc = NewRuc();
        var company = (await (await owner.PostAsJsonAsync("/api/v1/companies", new { ruc, details = new { legalName = "Emisora SAC", fiscalAddress = "Av. Larco 123", ubigeo = "150122" } })).Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/certificates", new { companyId = company.Id, pfxBase64 = PfxFor(ruc), password = "pw" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = SolPassword })).StatusCode);
        var series = (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = "01", code = "F001" })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;
        return new Setup(tenantId, owner, company, series);
    }

    private static async Task<DocumentDto> IssueAsync(Setup setup)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
        {
            Content = JsonContent.Create(new
            {
                seriesId = setup.InvoiceSeries.Id,
                issueDate = TodayInLima().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                currency = "PEN",
                buyer = new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
                lines = new[] { new { description = "Servicio", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10" } } },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await request.SendWith(setup.Owner);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
    }

    private static async Task<Created> NewHookAsync(HttpClient owner, string url, params string[] events)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/webhooks", new { url, description = "Integración de prueba", events = events.Length == 0 ? new[] { "document.issued", "document.accepted", "document.rejected" } : events });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Created>(ApiFixture.JsonOptions))!;
    }

    private static async Task<List<Delivery>> DeliveriesAsync(HttpClient owner, Guid hookId, string? state = null) =>
        (await owner.GetFromJsonAsync<List<Delivery>>($"/api/v1/webhooks/{hookId}/deliveries{(state is null ? string.Empty : "?state=" + state)}", ApiFixture.JsonOptions))!;

    /// <summary>The outbox hands the events to the consumers (the webhooks among them) and the dispatcher sends what is due.</summary>
    private async Task RunAsync(Guid tenantId, DateTimeOffset? at = null)
    {
        var outbox = new OutboxProcessor(api.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OutboxProcessor>.Instance);
        await outbox.RunOnceAsync(CancellationToken.None, tenantId);
        await outbox.RunOnceAsync(CancellationToken.None, tenantId);
        await api.Services.GetRequiredService<IWebhookDispatcher>().RunOnceAsync(at ?? DateTimeOffset.UtcNow.AddSeconds(1), CancellationToken.None);
    }

    /// <summary>
    /// Deletes the webhooks of the tenant, and their deliveries with them. A test that leaves failed deliveries to an address that is gone would make every later pass of the dispatcher (which takes the
    /// deliveries of all the tenants) wait for it: the connection to a closed port is slow to be refused on some systems.
    /// </summary>
    private static async Task CleanUpAsync(HttpClient owner)
    {
        foreach (var hook in (await owner.GetFromJsonAsync<List<Hook>>("/api/v1/webhooks", ApiFixture.JsonOptions))!)
        {
            await owner.DeleteAsync($"/api/v1/webhooks/{hook.Id}");
        }
    }

    private Task<int> DispatchAsync(DateTimeOffset at) => api.Services.GetRequiredService<IWebhookDispatcher>().RunOnceAsync(at, CancellationToken.None);

    [Fact]
    public async Task A_webhook_is_created_with_a_secret_shown_once_and_listed_without_it()
    {
        var (_, owner) = await TenantAsync("Webhooks SAC");

        var created = await NewHookAsync(owner, "https://hooks.cliente.pe/secure-fact", "document.accepted", "document.rejected");

        Assert.StartsWith("whsec_", created.Secret, StringComparison.Ordinal);
        Assert.Equal(created.Secret[^4..], created.Endpoint.SecretHint);
        Assert.Equal(["document.accepted", "document.rejected"], created.Endpoint.Events);
        var raw = await owner.GetStringAsync("/api/v1/webhooks");
        Assert.DoesNotContain(created.Secret, raw, StringComparison.Ordinal);
        Assert.Equal(created.Endpoint.Id, Assert.Single((await owner.GetFromJsonAsync<List<Hook>>("/api/v1/webhooks", ApiFixture.JsonOptions))!).Id);
        Assert.Equal(["document.issued", "document.accepted", "document.rejected"], (await owner.GetFromJsonAsync<List<string>>("/api/v1/webhooks/events", ApiFixture.JsonOptions))!);
    }

    [Fact]
    public async Task The_address_and_the_events_are_checked_and_a_webhook_can_be_edited_and_deleted()
    {
        var (_, owner) = await TenantAsync("Webhooks válidos SAC");
        foreach (var body in new object[]
        {
            new { url = "", events = new[] { "document.issued" } },
            new { url = "no es una url", events = new[] { "document.issued" } },
            new { url = "http://hooks.cliente.pe/x", events = new[] { "document.issued" } }, // allowed only for local targets, and this one is not local
            new { url = "https://usuario:clave@hooks.cliente.pe/x", events = new[] { "document.issued" } },
            new { url = "https://hooks.cliente.pe/x#fragmento", events = new[] { "document.issued" } },
            new { url = "https://hooks.cliente.pe/" + new string('a', 500), events = new[] { "document.issued" } },
            new { url = "https://hooks.cliente.pe/x", events = Array.Empty<string>() },
            new { url = "https://hooks.cliente.pe/x", events = new[] { "webhook.ping" } },
            new { url = "https://hooks.cliente.pe/x", events = new[] { "document.borrado" } },
            new { url = "https://hooks.cliente.pe/x", events = new[] { "document.issued" }, description = new string('x', 201) },
        })
        {
            var refused = await owner.PostAsJsonAsync("/api/v1/webhooks", body);
            // The plain http address is refused only when local targets are not allowed (the tests allow them): the rest is refused always.
            if (JsonSerializer.Serialize(body).Contains("http://hooks.cliente.pe", StringComparison.Ordinal))
            {
                Assert.Equal(HttpStatusCode.Created, refused.StatusCode);
                continue;
            }

            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("SF-WHK-001", await CodeAsync(refused));
        }

        var existing = (await owner.GetFromJsonAsync<List<Hook>>("/api/v1/webhooks", ApiFixture.JsonOptions))!;
        var first = Assert.Single(existing);
        var edited = await owner.PutAsJsonAsync($"/api/v1/webhooks/{first.Id}", new { url = "https://hooks.cliente.pe/otro", description = "Nueva", events = new[] { "document.accepted" }, isActive = false });
        var row = (await edited.Content.ReadFromJsonAsync<Hook>(ApiFixture.JsonOptions))!;
        Assert.Equal(("https://hooks.cliente.pe/otro", "Nueva", false), (row.Url, row.Description, row.IsActive));
        Assert.NotNull(row.DisabledAt);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/webhooks/{first.Id}")).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<List<Hook>>("/api/v1/webhooks", ApiFixture.JsonOptions))!);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/v1/webhooks/{first.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync($"/api/v1/webhooks/{Guid.NewGuid()}", new { url = "https://hooks.cliente.pe/x", events = new[] { "document.issued" } })).StatusCode);
    }

    [Fact]
    public async Task A_tenant_has_a_limited_number_of_webhooks()
    {
        var (_, owner) = await TenantAsync("Muchos webhooks SAC");
        for (var i = 0; i < 10; i++)
        {
            await NewHookAsync(owner, $"https://hooks.cliente.pe/{i}", "document.issued");
        }

        var refused = await owner.PostAsJsonAsync("/api/v1/webhooks", new { url = "https://hooks.cliente.pe/once", events = new[] { "document.issued" } });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("SF-WHK-003", await CodeAsync(refused));
    }

    [Fact]
    public async Task Only_a_person_with_the_permission_manages_the_webhooks_and_a_tenant_sees_only_its_own()
    {
        using var admin = await api.AdminClientAsync();
        var (tenantA, ownerA) = await TenantAsync("Webhooks A SAC");
        var (_, ownerB) = await TenantAsync("Webhooks B SAC");
        var hookA = await NewHookAsync(ownerA, "https://hooks.a.pe/x", "document.issued");
        var seller = await ApiFixture.CreateUserAsync(ownerA, Roles.Sales, tenantA);
        using var sellerClient = api.ClientFor(await api.LoginOkAsync(seller.Email, seller.Password));
        var key = (await (await ownerA.PostAsJsonAsync("/api/v1/api-keys", new { name = "Programa", role = "BillingAdmin" })).Content.ReadFromJsonAsync<JsonElement>(ApiFixture.JsonOptions)).GetProperty("secret").GetString()!;
        using var keyClient = api.NewClient();
        keyClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        // A person without the permission, a key, platform staff and an anonymous caller manage nothing.
        foreach (var client in new[] { sellerClient, keyClient, admin })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/webhooks")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/webhooks", new { url = "https://hooks.mal.pe/x", events = new[] { "document.issued" } })).StatusCode);
        }

        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/webhooks")).StatusCode);

        // Another tenant does not see it, edit it, try it, rotate it nor delete it: for it the webhook does not exist.
        Assert.Empty((await ownerB.GetFromJsonAsync<List<Hook>>("/api/v1/webhooks", ApiFixture.JsonOptions))!);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.PutAsJsonAsync($"/api/v1/webhooks/{hookA.Endpoint.Id}", new { url = "https://hooks.mal.pe/x", events = new[] { "document.issued" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.PostAsync($"/api/v1/webhooks/{hookA.Endpoint.Id}/test", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.PostAsync($"/api/v1/webhooks/{hookA.Endpoint.Id}/rotate-secret", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.GetAsync($"/api/v1/webhooks/{hookA.Endpoint.Id}/deliveries")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.DeleteAsync($"/api/v1/webhooks/{hookA.Endpoint.Id}")).StatusCode);
        Assert.Single((await ownerA.GetFromJsonAsync<List<Hook>>("/api/v1/webhooks", ApiFixture.JsonOptions))!);
    }

    [Fact]
    public async Task A_test_reaches_the_address_signed_and_says_how_it_went()
    {
        var (_, owner) = await TenantAsync("Prueba de webhook SAC");
        await using var receiver = await WebhookReceiver.StartAsync("ping");
        var created = await NewHookAsync(owner, receiver.Url);

        var response = await owner.PostAsync($"/api/v1/webhooks/{created.Endpoint.Id}/test", null);
        var delivery = (await response.Content.ReadFromJsonAsync<Delivery>(ApiFixture.JsonOptions))!;

        Assert.Equal(("Delivered", 1, 200, "webhook.ping"), (delivery.State, delivery.Attempts, delivery.LastStatusCode, delivery.EventType));
        var received = Assert.Single(receiver.Requests);
        Assert.Equal("/ping", received.Path);
        Assert.Equal("webhook.ping", received.Header("X-SecureFact-Event"));
        Assert.Equal(delivery.Id.ToString(), received.Header("X-SecureFact-Delivery"));
        Assert.True(WebhookReceiver.IsAuthentic(received, created.Secret));
        Assert.False(WebhookReceiver.IsAuthentic(received, "whsec_otro"));
        Assert.StartsWith("SecureFact-Webhooks/", received.Header("User-Agent"), StringComparison.Ordinal);
        Assert.True(Math.Abs(long.Parse(received.Header("X-SecureFact-Timestamp"), System.Globalization.CultureInfo.InvariantCulture) - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) < 60);
        using var body = JsonDocument.Parse(received.Body);
        Assert.Equal(("webhook.ping", "v1"), (body.RootElement.GetProperty("type").GetString(), body.RootElement.GetProperty("apiVersion").GetString()));
        Assert.Equal(delivery.EventId, body.RootElement.GetProperty("id").GetGuid());

        // An address that answers with an error: the test says so, with the status.
        receiver.Status = 503;
        var failing = (await (await owner.PostAsync($"/api/v1/webhooks/{created.Endpoint.Id}/test", null)).Content.ReadFromJsonAsync<Delivery>(ApiFixture.JsonOptions))!;
        Assert.Equal(("Failed", 503, "HTTP 503"), (failing.State, failing.LastStatusCode, failing.LastError));
        Assert.NotNull(failing.NextAttemptAt);

        // An address that does not answer at all.
        var closed = await NewHookAsync(owner, "http://127.0.0.1:1/nadie");
        var unreachable = (await (await owner.PostAsync($"/api/v1/webhooks/{closed.Endpoint.Id}/test", null)).Content.ReadFromJsonAsync<Delivery>(ApiFixture.JsonOptions))!;
        Assert.Equal(("Failed", null, "No se pudo conectar con la dirección."), (unreachable.State, unreachable.LastStatusCode, unreachable.LastError));

        // A webhook that is off is not tried.
        await owner.PutAsJsonAsync($"/api/v1/webhooks/{created.Endpoint.Id}", new { url = receiver.Url, events = new[] { "document.issued" }, isActive = false });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsync($"/api/v1/webhooks/{created.Endpoint.Id}/test", null)).StatusCode);
        await CleanUpAsync(owner);
    }

    [Fact]
    public async Task An_issued_document_is_announced_to_the_webhooks_that_asked_for_it_and_only_to_them_and_once()
    {
        var setup = await IssuerAsync("Webhook de emisión SAC");
        await using var wanted = await WebhookReceiver.StartAsync();
        await using var other = await WebhookReceiver.StartAsync();
        var asked = await NewHookAsync(setup.Owner, wanted.Url, "document.issued");
        var notAsked = await NewHookAsync(setup.Owner, other.Url, "document.accepted");
        var off = await NewHookAsync(setup.Owner, wanted.Url + "/apagado", "document.issued");
        await setup.Owner.PutAsJsonAsync($"/api/v1/webhooks/{off.Endpoint.Id}", new { url = wanted.Url + "/apagado", events = new[] { "document.issued" }, isActive = false });

        var document = await IssueAsync(setup);
        await RunAsync(setup.TenantId);
        await RunAsync(setup.TenantId);

        var request = Assert.Single(wanted.Requests, r => r.Path == "/hook");
        Assert.Empty(other.Requests);
        Assert.DoesNotContain(wanted.Requests, r => r.Path == "/apagado");
        Assert.True(WebhookReceiver.IsAuthentic(request, asked.Secret));
        Assert.False(WebhookReceiver.IsAuthentic(request, notAsked.Secret));
        Assert.Equal("document.issued", request.Header("X-SecureFact-Event"));
        using var body = JsonDocument.Parse(request.Body);
        var data = body.RootElement.GetProperty("data");
        Assert.Equal(("document.issued", setup.TenantId), (body.RootElement.GetProperty("type").GetString(), body.RootElement.GetProperty("tenantId").GetGuid()));
        Assert.Equal((document.Id, "01", "F001", 1), (data.GetProperty("documentId").GetGuid(), data.GetProperty("documentTypeCode").GetString(), data.GetProperty("series").GetString(), data.GetProperty("number").GetInt32()));
        // The delivery that was made is the one that the person sees, once.
        Assert.Equal("Delivered", Assert.Single(await DeliveriesAsync(setup.Owner, asked.Endpoint.Id)).State);
        Assert.Empty(await DeliveriesAsync(setup.Owner, notAsked.Endpoint.Id));
    }

    [Fact]
    public async Task The_answer_of_sunat_is_announced_as_accepted_or_rejected_with_its_code()
    {
        var setup = await IssuerAsync("Webhook de respuesta SAC");
        await using var receiver = await WebhookReceiver.StartAsync();
        var created = await NewHookAsync(setup.Owner, receiver.Url, "document.accepted", "document.rejected");

        var accepted = await IssueAsync(setup);
        await RunAsync(setup.TenantId);
        var electronic = (await setup.Owner.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/documents/{accepted.Id}/electronic", ApiFixture.JsonOptions))!;
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, $"{accepted.Series}-{accepted.Number}")));
        Assert.Equal(HttpStatusCode.OK, (await setup.Owner.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/send", null)).StatusCode);
        await RunAsync(setup.TenantId);

        var rejected = await IssueAsync(setup);
        await RunAsync(setup.TenantId);
        var second = (await setup.Owner.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/documents/{rejected.Id}/electronic", ApiFixture.JsonOptions))!;
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, $"{rejected.Series}-{rejected.Number}", "2335", "El comprobante fue rechazado")));
        await setup.Owner.PostAsync($"/api/v1/electronic-documents/{second.Id}/send", null);
        await RunAsync(setup.TenantId);

        var events = receiver.Requests.Select(r => (r.Header("X-SecureFact-Event"), r.Body)).ToList();
        var accept = Assert.Single(events, e => e.Item1 == "document.accepted");
        var reject = Assert.Single(events, e => e.Item1 == "document.rejected");
        using var acceptedBody = JsonDocument.Parse(accept.Body);
        var sunat = acceptedBody.RootElement.GetProperty("data");
        Assert.Equal((accepted.Id, "Accepted", 0), (sunat.GetProperty("documentId").GetGuid(), sunat.GetProperty("state").GetString(), sunat.GetProperty("sunat").GetProperty("code").GetInt32()));
        using var rejectedBody = JsonDocument.Parse(reject.Body);
        var refused = rejectedBody.RootElement.GetProperty("data");
        Assert.Equal((rejected.Id, "Rejected", 2335), (refused.GetProperty("documentId").GetGuid(), refused.GetProperty("state").GetString(), refused.GetProperty("sunat").GetProperty("code").GetInt32()));
        Assert.All(receiver.Requests, r => Assert.True(WebhookReceiver.IsAuthentic(r, created.Secret)));
    }

    [Fact]
    public async Task A_failing_address_is_tried_again_with_a_growing_wait_until_the_delivery_dies_and_a_person_can_send_it_again()
    {
        var (_, owner) = await TenantAsync("Reintentos de webhook SAC");
        await using var receiver = await WebhookReceiver.StartAsync();
        receiver.Status = 500;
        var created = await NewHookAsync(owner, receiver.Url, "document.issued");
        var ping = (await (await owner.PostAsync($"/api/v1/webhooks/{created.Endpoint.Id}/test", null)).Content.ReadFromJsonAsync<Delivery>(ApiFixture.JsonOptions))!;
        Assert.Equal(("Failed", 1), (ping.State, ping.Attempts));

        // Each pass finds it due (the clock jumps a week) and counts one more attempt. After the first failure the wait was 1 minute; after the 2nd to the 7th it is 5 minutes, 30, 2 hours, 6, 12 and 24.
        var waits = new List<TimeSpan>();
        for (var attempt = 2; attempt <= 8; attempt++)
        {
            await DispatchAsync(DateTimeOffset.UtcNow.AddDays(7 * attempt));
            var now = (await DeliveriesAsync(owner, created.Endpoint.Id)).Single();
            Assert.Equal(attempt, now.Attempts);
            if (attempt < 8)
            {
                waits.Add(now.NextAttemptAt!.Value - DateTimeOffset.UtcNow);
            }
        }

        Assert.Equal(6, waits.Count);
        TimeSpan[] expected = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromHours(24)];
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.InRange(waits[i], expected[i] - TimeSpan.FromMinutes(3), expected[i]);
        }
        var dead = (await DeliveriesAsync(owner, created.Endpoint.Id)).Single();
        Assert.Equal(("Dead", 8, 500, null), (dead.State, dead.Attempts, dead.LastStatusCode, dead.NextAttemptAt));
        Assert.Equal(0, await DispatchAsync(DateTimeOffset.UtcNow.AddDays(90))); // a dead delivery is not taken again

        // The receiver is fixed and a person sends the delivery again: the attempts start from zero.
        receiver.Status = 200;
        var redelivered = await owner.PostAsync($"/api/v1/webhooks/deliveries/{dead.Id}/redeliver", null);
        Assert.Equal("Pending", (await redelivered.Content.ReadFromJsonAsync<Delivery>(ApiFixture.JsonOptions))!.State);
        await DispatchAsync(DateTimeOffset.UtcNow.AddSeconds(5));
        var delivered = (await DeliveriesAsync(owner, created.Endpoint.Id)).Single();
        Assert.Equal(("Delivered", 1, 200), (delivered.State, delivered.Attempts, delivered.LastStatusCode));
        // A delivery that is waiting for its turn is not sent twice.
        var again = await owner.PostAsync($"/api/v1/webhooks/deliveries/{dead.Id}/redeliver", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/v1/webhooks/deliveries/{dead.Id}/redeliver", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/v1/webhooks/deliveries/{Guid.NewGuid()}/redeliver", null)).StatusCode);
        await CleanUpAsync(owner);
    }

    [Fact]
    public async Task An_address_that_answers_gone_is_switched_off_and_one_that_fails_forty_times_in_a_row_too()
    {
        var (_, owner) = await TenantAsync("Webhook apagado SAC");
        await using var gone = await WebhookReceiver.StartAsync();
        gone.Status = 410;
        var first = await NewHookAsync(owner, gone.Url);
        var ping = (await (await owner.PostAsync($"/api/v1/webhooks/{first.Endpoint.Id}/test", null)).Content.ReadFromJsonAsync<Delivery>(ApiFixture.JsonOptions))!;
        Assert.Equal(("Dead", 410), (ping.State, ping.LastStatusCode));
        var off = (await owner.GetFromJsonAsync<List<Hook>>("/api/v1/webhooks", ApiFixture.JsonOptions))!.Single(h => h.Id == first.Endpoint.Id);
        Assert.False(off.IsActive);
        Assert.Contains("410", off.DisabledReason, StringComparison.Ordinal);

        await using var down = await WebhookReceiver.StartAsync();
        down.Status = 500;
        var second = await NewHookAsync(owner, down.Url);
        for (var i = 0; i < 40; i++)
        {
            await owner.PostAsync($"/api/v1/webhooks/{second.Endpoint.Id}/test", null);
        }

        var disabled = (await owner.GetFromJsonAsync<List<Hook>>("/api/v1/webhooks", ApiFixture.JsonOptions))!.Single(h => h.Id == second.Endpoint.Id);
        Assert.Equal((false, 40), (disabled.IsActive, disabled.ConsecutiveFailures));
        Assert.Contains("40", disabled.DisabledReason, StringComparison.Ordinal);

        // Switching it on again clears the count; a success also does.
        down.Status = 200;
        var back = (await (await owner.PutAsJsonAsync($"/api/v1/webhooks/{second.Endpoint.Id}", new { url = down.Url, events = new[] { "document.issued" }, isActive = true })).Content.ReadFromJsonAsync<Hook>(ApiFixture.JsonOptions))!;
        Assert.True(back.IsActive);
        Assert.Equal(0, back.ConsecutiveFailures);
        Assert.Null(back.DisabledReason);
        await CleanUpAsync(owner);
    }

    [Fact]
    public async Task Rotating_the_secret_changes_what_signs_the_next_deliveries()
    {
        var (_, owner) = await TenantAsync("Rotar secreto SAC");
        await using var receiver = await WebhookReceiver.StartAsync();
        var created = await NewHookAsync(owner, receiver.Url);

        var rotated = (await (await owner.PostAsync($"/api/v1/webhooks/{created.Endpoint.Id}/rotate-secret", null)).Content.ReadFromJsonAsync<Created>(ApiFixture.JsonOptions))!;
        await owner.PostAsync($"/api/v1/webhooks/{created.Endpoint.Id}/test", null);

        Assert.NotEqual(created.Secret, rotated.Secret);
        Assert.Equal(rotated.Secret[^4..], rotated.Endpoint.SecretHint);
        var request = Assert.Single(receiver.Requests);
        Assert.True(WebhookReceiver.IsAuthentic(request, rotated.Secret));
        Assert.False(WebhookReceiver.IsAuthentic(request, created.Secret));
    }

    [Fact]
    public async Task The_secret_is_kept_encrypted_and_never_reaches_the_audit_trail_or_the_logs()
    {
        using var admin = await api.AdminClientAsync();
        var (tenantId, owner) = await TenantAsync("Secreto cifrado SAC");
        var created = await NewHookAsync(owner, "https://hooks.cliente.pe/cifrado", "document.issued");
        await owner.PostAsync($"/api/v1/webhooks/{created.Endpoint.Id}/rotate-secret", null);

        await using var connection = new NpgsqlConnection(api.Postgres.OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT secret_ciphertext FROM webhook.endpoint WHERE id = '{created.Endpoint.Id}'", connection);
        var stored = (byte[])(await command.ExecuteScalarAsync())!;
        Assert.DoesNotContain(created.Secret, System.Text.Encoding.Latin1.GetString(stored), StringComparison.Ordinal);

        var events = (await admin.GetFromJsonAsync<List<JsonElement>>($"/api/v1/audit?tenantId={tenantId}&entityType=webhook&take=20", ApiFixture.JsonOptions))!;
        Assert.Equal(["webhooks.webhook.secret_rotated", "webhooks.webhook.created"], events.Select(e => e.GetProperty("action").GetString()));
        Assert.DoesNotContain("whsec_", JsonSerializer.Serialize(events), StringComparison.Ordinal);
        Assert.DoesNotContain(api.Logs.Snapshot(), entry => entry.Contains(created.Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Where_local_targets_are_not_allowed_an_address_that_points_inside_is_refused_when_it_is_registered()
    {
        var (_, owner) = await TenantAsync("Destinos privados SAC");
        var previous = Environment.GetEnvironmentVariable("Webhooks__AllowLocalTargets");
        Environment.SetEnvironmentVariable("Webhooks__AllowLocalTargets", "false");
        try
        {
            await using var strict = new WebApplicationFactory<Program>();
            using var admin = await api.AdminClientAsync();
            var tenantId = await api.CreateTenantAsync("Destinos estrictos SAC");
            var person = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
            using var client = strict.CreateClient();
            var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = person.Email, password = person.Password });
            var tokens = (await login.Content.ReadFromJsonAsync<AuthTokens>(ApiFixture.JsonOptions))!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

            foreach (var url in new[]
            {
                "https://localhost/hook", "https://127.0.0.1/hook", "https://10.0.0.5/hook", "https://192.168.1.10/hook", "https://172.16.0.1/hook", "https://169.254.169.254/latest/meta-data",
                "https://[::1]/hook", "https://[fd00::1]/hook", "https://servicio.internal/hook", "https://impresora.local/hook", "http://hooks.cliente.pe/hook", "https://100.64.0.1/hook",
            })
            {
                var refused = await client.PostAsJsonAsync("/api/v1/webhooks", new { url, events = new[] { "document.issued" } });
                Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
                Assert.Equal("SF-WHK-001", await CodeAsync(refused));
            }

            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/webhooks", new { url = "https://hooks.cliente.pe/hook", events = new[] { "document.issued" } })).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Webhooks__AllowLocalTargets", previous);
        }
    }

    [Fact]
    public async Task The_deliveries_of_a_webhook_are_listed_and_filtered_by_state()
    {
        var (_, owner) = await TenantAsync("Entregas de webhook SAC");
        await using var receiver = await WebhookReceiver.StartAsync();
        var created = await NewHookAsync(owner, receiver.Url);
        await owner.PostAsync($"/api/v1/webhooks/{created.Endpoint.Id}/test", null);
        receiver.Status = 500;
        await owner.PostAsync($"/api/v1/webhooks/{created.Endpoint.Id}/test", null);

        Assert.Equal(2, (await DeliveriesAsync(owner, created.Endpoint.Id)).Count);
        Assert.Equal("Delivered", Assert.Single(await DeliveriesAsync(owner, created.Endpoint.Id, "Delivered")).State);
        Assert.Equal("Failed", Assert.Single(await DeliveriesAsync(owner, created.Endpoint.Id, "Failed")).State);
        Assert.Empty(await DeliveriesAsync(owner, created.Endpoint.Id, "Dead"));
        await CleanUpAsync(owner);
    }
}

internal static class RequestExtensions
{
    public static Task<HttpResponseMessage> SendWith(this HttpRequestMessage request, HttpClient client) => client.SendAsync(request);
}
