using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SecureFact.Gre.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;

namespace SecureFact.Security.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class GreApiTests(ApiFixture api)
{
    private const string SolPassword = "Sol-Clave-gre-secret-9";
    private const string ApiSecret = "api-client-secret-gre-5521";
    private static int _rucCounter = 8_000_000;

    private sealed record Setup(Guid TenantId, HttpClient Owner, CompanyDto Company, GreSeriesDto Series);

    private static string NewRuc()
    {
        var body = "20" + Interlocked.Increment(ref _rucCounter).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var sum = body.Select((c, i) => (c - '0') * weights[i]).Sum();
        return body + ((11 - (sum % 11)) % 10);
    }

    private static string Today() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Lima")).DateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string PfxFor(string ruc)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=Representante Demo, OU={ruc}, O=EMISORA SAC, C=PE", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, "pw"));
    }

    private async Task<Setup> NewTenantAsync(string name, bool apiCredentials = true, bool certificate = true)
    {
        api.Gre.Reset();
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

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = SolPassword })).StatusCode);
        if (apiCredentials)
        {
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/v1/sol-credentials/{company.Id}/api", new { clientId = "client-id-gre", clientSecret = ApiSecret })).StatusCode);
        }

        var series = await SeriesAsync(owner, company.Id, "T001");
        return new Setup(tenantId, owner, company, series);
    }

    private static async Task<GreSeriesDto> SeriesAsync(HttpClient owner, Guid companyId, string code)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/gre/series", new { companyId, code });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GreSeriesDto>(ApiFixture.JsonOptions))!;
    }

    private static object Guide(Setup setup, string motive = "01", string? note = null) => new
    {
        companyId = setup.Company.Id,
        seriesId = setup.Series.Id,
        motiveCode = motive,
        modalityCode = "02",
        transferStartDate = Today(),
        grossWeight = 12.5m,
        weightUnit = "KGM",
        packageCount = 3,
        note,
        recipient = new { documentTypeCode = "6", documentNumber = "20100070970", name = "CLIENTE DEMO SAC" },
        origin = new { ubigeoCode = "150101", address = "Av. Argentina 123, Lima" },
        destination = new { ubigeoCode = "150122", address = "Calle Los Pinos 456, Miraflores" },
        vehicle = new { plate = "ABC123", circulationCard = "1234567890" },
        driver = new { documentTypeCode = "1", documentNumber = "12345678", firstNames = "JUAN CARLOS", lastNames = "PEREZ GOMEZ", licenseNumber = "Q12345678" },
        goods = new[] { new { description = "Caja de repuestos", unitCode = "NIU", quantity = 3m, code = "REP-01" } },
        relatedDocuments = new[] { new { typeCode = "01", number = "F001-123", issuerRuc = setup.Company.Ruc } },
    };

    private static async Task<GreDto> CreateOkAsync(Setup setup, string? note = null)
    {
        var response = await setup.Owner.PostAsJsonAsync("/api/v1/gre/guides", Guide(setup, note: note));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<GreDto>(ApiFixture.JsonOptions))!;
    }

    private static async Task<GreDto> ReadAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/v1/gre/guides/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GreDto>(ApiFixture.JsonOptions))!;
    }

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task A_series_of_the_sender_starts_with_T_and_is_not_repeated()
    {
        var setup = await NewTenantAsync("gre-series");

        var invoiceShaped = await setup.Owner.PostAsJsonAsync("/api/v1/gre/series", new { companyId = setup.Company.Id, code = "F001" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invoiceShaped.StatusCode);
        Assert.Equal("SF-GRE-005", await ProblemCodeAsync(invoiceShaped));

        var repeated = await setup.Owner.PostAsJsonAsync("/api/v1/gre/series", new { companyId = setup.Company.Id, code = "t001" });
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        Assert.Equal("SF-GRE-007", await ProblemCodeAsync(repeated));

        var listed = await setup.Owner.GetFromJsonAsync<GreSeriesDto[]>($"/api/v1/gre/series?companyId={setup.Company.Id}", ApiFixture.JsonOptions);
        Assert.Single(listed!);
    }

    [Fact]
    public async Task A_guide_is_numbered_in_order_and_stored_signed_and_prepared()
    {
        var setup = await NewTenantAsync("gre-create");

        var first = await CreateOkAsync(setup);
        var second = await CreateOkAsync(setup);

        Assert.Equal(GreState.Prepared, first.State);
        Assert.Equal("T001-1", first.Name);
        Assert.Equal("T001-2", second.Name);

        var xml = await setup.Owner.GetStringAsync($"/api/v1/gre/guides/{first.Id}/xml");
        Assert.Contains("DespatchAdvice", xml, StringComparison.Ordinal);
        Assert.Contains("<SignatureValue", xml.Replace("ds:SignatureValue", "SignatureValue"), StringComparison.Ordinal);
        Assert.Contains("T001-1", xml, StringComparison.Ordinal);
        Assert.Empty(api.Gre.Submissions);
    }

    [Fact]
    public async Task A_guide_with_a_motive_that_is_not_supported_yet_is_refused_without_taking_a_number()
    {
        var setup = await NewTenantAsync("gre-motive");

        var response = await setup.Owner.PostAsJsonAsync("/api/v1/gre/guides", Guide(setup, motive: "08"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("SF-GRE-003", await ProblemCodeAsync(response));
        var next = await CreateOkAsync(setup);
        Assert.Equal("T001-1", next.Name);
    }

    [Fact]
    public async Task A_guide_that_breaks_the_rules_is_refused_with_the_reasons()
    {
        var setup = await NewTenantAsync("gre-invalid");

        var broken = await setup.Owner.PostAsJsonAsync("/api/v1/gre/guides", new
        {
            companyId = setup.Company.Id,
            seriesId = setup.Series.Id,
            motiveCode = "01",
            modalityCode = "02",
            transferStartDate = Today(),
            grossWeight = 0m,
            weightUnit = "KGM",
            recipient = new { documentTypeCode = "6", documentNumber = "20100070970", name = "CLIENTE DEMO SAC" },
            origin = new { ubigeoCode = "150101", address = "Av. Argentina 123, Lima" },
            destination = new { ubigeoCode = "150122", address = "Calle Los Pinos 456, Miraflores" },
            goods = Array.Empty<object>(),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, broken.StatusCode);
        Assert.Equal("SF-GRE-001", await ProblemCodeAsync(broken));
    }

    [Fact]
    public async Task Sending_needs_the_credentials_of_the_API_of_SUNAT()
    {
        var setup = await NewTenantAsync("gre-no-api", apiCredentials: false);
        var guide = await CreateOkAsync(setup);

        var response = await setup.Owner.PostAsync($"/api/v1/gre/guides/{guide.Id}/submit", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("SF-GRE-008", await ProblemCodeAsync(response));
        Assert.Equal(GreState.Prepared, (await ReadAsync(setup.Owner, guide.Id)).State);
        Assert.Empty(api.Gre.Submissions);
    }

    [Fact]
    public async Task A_guide_that_SUNAT_accepts_keeps_its_CDR()
    {
        var setup = await NewTenantAsync("gre-accept");
        var guide = await CreateOkAsync(setup);

        var response = await setup.Owner.PostAsync($"/api/v1/gre/guides/{guide.Id}/submit", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<GreDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(GreState.Accepted, accepted.State);
        Assert.Equal(0, accepted.CdrResponseCode);
        Assert.NotNull(accepted.Ticket);

        var submission = Assert.Single(api.Gre.Submissions);
        Assert.Equal("09", submission.DocumentTypeCode);
        Assert.Equal($"{setup.Company.Ruc}-09-T001-1", submission.FileBaseName);
        Assert.Equal("client-id-gre", submission.Credentials.ClientId);
        Assert.Equal(ApiSecret, submission.Credentials.ClientSecret);

        var cdr = await setup.Owner.GetAsync($"/api/v1/gre/guides/{guide.Id}/cdr");
        Assert.Equal(HttpStatusCode.OK, cdr.StatusCode);
        Assert.Equal("application/zip", cdr.Content.Headers.ContentType?.MediaType);

        var again = await setup.Owner.PostAsync($"/api/v1/gre/guides/{guide.Id}/submit", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("SF-GRE-004", await ProblemCodeAsync(again));
        Assert.Single(api.Gre.Submissions);
    }

    [Fact]
    public async Task An_observed_guide_and_a_rejected_one_take_the_state_of_their_CDR()
    {
        var setup = await NewTenantAsync("gre-cdr-states");
        var observed = await CreateOkAsync(setup, note: "[sandbox:observar]");
        var rejected = await CreateOkAsync(setup, note: "[sandbox:rechazar-cdr]");

        var observedResult = (await (await setup.Owner.PostAsync($"/api/v1/gre/guides/{observed.Id}/submit", null)).Content.ReadFromJsonAsync<GreDto>(ApiFixture.JsonOptions))!;
        var rejectedResult = (await (await setup.Owner.PostAsync($"/api/v1/gre/guides/{rejected.Id}/submit", null)).Content.ReadFromJsonAsync<GreDto>(ApiFixture.JsonOptions))!;

        Assert.Equal(GreState.AcceptedWithObservations, observedResult.State);
        Assert.Contains(observedResult.Observations, o => o.Code == "4030");
        Assert.Equal(GreState.Rejected, rejectedResult.State);
        Assert.Equal(2800, rejectedResult.CdrResponseCode);
    }

    [Fact]
    public async Task A_send_that_SUNAT_refuses_fails_the_guide_for_good()
    {
        var setup = await NewTenantAsync("gre-refused");
        var guide = await CreateOkAsync(setup, note: "[sandbox:rechazar]");

        var response = await setup.Owner.PostAsync($"/api/v1/gre/guides/{guide.Id}/submit", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var failed = (await response.Content.ReadFromJsonAsync<GreDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(GreState.Failed, failed.State);
        Assert.Equal("502", failed.ErrorCode);
    }

    [Fact]
    public async Task A_transient_failure_keeps_the_guide_prepared_and_the_worker_sends_it_later()
    {
        var setup = await NewTenantAsync("gre-transient");
        var guide = await CreateOkAsync(setup);
        api.Gre.EnqueueSubmit(new GreSubmitOutcome(GreSubmitStatus.Transient, null, "SF-GRE-NETWORK", "No se pudo conectar con SUNAT."));

        var response = await setup.Owner.PostAsync($"/api/v1/gre/guides/{guide.Id}/submit", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("SF-GRE-011", await ProblemCodeAsync(response));
        var waiting = await ReadAsync(setup.Owner, guide.Id);
        Assert.Equal(GreState.Prepared, waiting.State);
        Assert.Equal(1, waiting.Attempts);

        await api.Postgres.ExecuteAsOwnerAsync($"UPDATE gre.guide SET next_attempt_at = now() - interval '1 hour' WHERE id = '{guide.Id}'");
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var changed = await scope.ServiceProvider.GetRequiredService<IGreWorkProcessor>().RunOnceAsync(CancellationToken.None);
            Assert.True(changed >= 1);
        }

        Assert.Equal(GreState.Accepted, (await ReadAsync(setup.Owner, guide.Id)).State);
    }

    [Fact]
    public async Task A_guide_waiting_for_its_ticket_is_closed_by_the_worker()
    {
        var setup = await NewTenantAsync("gre-pending");
        var guide = await CreateOkAsync(setup);
        await api.Postgres.ExecuteAsOwnerAsync($"UPDATE gre.guide SET state = 'Pending', ticket = 'SBX.' || translate(encode(convert_to('{setup.Company.Ruc}/T001-1/A', 'UTF8'), 'base64'), '+/=', '-_'), next_attempt_at = now() - interval '1 hour' WHERE id = '{guide.Id}'");

        await using (var scope = api.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IGreWorkProcessor>().RunOnceAsync(CancellationToken.None);
        }

        var closed = await ReadAsync(setup.Owner, guide.Id);
        Assert.Equal(GreState.Accepted, closed.State);
        Assert.NotNull(closed.ProcessedAt);
    }

    [Fact]
    public async Task One_tenant_never_sees_the_guides_or_the_series_of_another()
    {
        var one = await NewTenantAsync("gre-iso-one");
        var other = await NewTenantAsync("gre-iso-other");
        var guide = await CreateOkAsync(one);

        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.GetAsync($"/api/v1/gre/guides/{guide.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.PostAsync($"/api/v1/gre/guides/{guide.Id}/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.GetAsync($"/api/v1/gre/guides/{guide.Id}/xml")).StatusCode);
        Assert.DoesNotContain((await other.Owner.GetFromJsonAsync<GreDto[]>("/api/v1/gre/guides", ApiFixture.JsonOptions))!, g => g.Id == guide.Id);

        var crossed = await other.Owner.PostAsJsonAsync("/api/v1/gre/guides", new
        {
            companyId = other.Company.Id,
            seriesId = one.Series.Id,
            motiveCode = "01",
            modalityCode = "02",
        });
        Assert.NotEqual(HttpStatusCode.Created, crossed.StatusCode);
    }

    [Fact]
    public async Task The_database_keeps_the_signed_guide_and_its_final_answer_immutable()
    {
        var setup = await NewTenantAsync("gre-immutable");
        var prepared = await CreateOkAsync(setup);
        var accepted = await CreateOkAsync(setup);
        Assert.Equal(HttpStatusCode.OK, (await setup.Owner.PostAsync($"/api/v1/gre/guides/{accepted.Id}/submit", null)).StatusCode);

        var xml = await Assert.ThrowsAsync<PostgresException>(() => api.Postgres.ExecuteAsOwnerAsync($"UPDATE gre.guide SET signed_xml = '<x/>' WHERE id = '{prepared.Id}'"));
        var final = await Assert.ThrowsAsync<PostgresException>(() => api.Postgres.ExecuteAsOwnerAsync($"UPDATE gre.guide SET error_message = 'x' WHERE id = '{accepted.Id}'"));
        var delete = await Assert.ThrowsAsync<PostgresException>(() => api.Postgres.ExecuteAsOwnerAsync($"DELETE FROM gre.guide WHERE id = '{prepared.Id}'"));
        var rewind = await Assert.ThrowsAsync<PostgresException>(() => api.Postgres.ExecuteAsOwnerAsync($"UPDATE gre.series SET last_number = 0 WHERE id = '{setup.Series.Id}'"));

        Assert.All([xml, final, delete, rewind], e => Assert.Equal("42501", e.SqlState));
    }

    [Fact]
    public async Task The_secrets_of_the_API_never_leave_in_the_guide_or_the_credentials_answer()
    {
        var setup = await NewTenantAsync("gre-secrets");
        var guide = await CreateOkAsync(setup);
        await setup.Owner.PostAsync($"/api/v1/gre/guides/{guide.Id}/submit", null);

        var credentials = await setup.Owner.GetStringAsync($"/api/v1/sol-credentials/{setup.Company.Id}");
        var detail = await setup.Owner.GetStringAsync($"/api/v1/gre/guides/{guide.Id}");

        Assert.DoesNotContain(ApiSecret, credentials, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiSecret, detail, StringComparison.Ordinal);
        Assert.Contains("hasApiSecret", credentials, StringComparison.Ordinal);
        Assert.DoesNotContain(api.Logs.Snapshot(), m => m.Contains(ApiSecret, StringComparison.Ordinal));
    }
}
