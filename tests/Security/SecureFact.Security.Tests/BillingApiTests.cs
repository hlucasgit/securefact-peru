using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using SecureFact.Billing.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;
using SecureFact.SharedKernel.Domain;

namespace SecureFact.Security.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class BillingApiTests(ApiFixture api)
{
    private static int _rucCounter = 30_000_000;

    private static string NewRuc()
    {
        var body = "20" + Interlocked.Increment(ref _rucCounter).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var sum = body.Select((c, i) => (c - '0') * weights[i]).Sum();
        var ruc = body + ((11 - (sum % 11)) % 10);
        Assert.True(Ruc.Create(ruc).IsSuccess);
        return ruc;
    }

    private static DateOnly TodayInLima() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Lima")).DateTime);

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private sealed record Setup(Guid TenantId, HttpClient Owner, Guid CompanyId, SeriesDto InvoiceSeries, SeriesDto ReceiptSeries);

    private async Task<Setup> NewTenantWithSeriesAsync(string name)
    {
        var tenantId = await api.CreateTenantAsync(name);
        using var admin = await api.AdminClientAsync();
        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        var owner = api.ClientFor(await api.LoginOkAsync(user.Email, user.Password));

        var companyResponse = await owner.PostAsJsonAsync("/api/v1/companies", new
        {
            ruc = NewRuc(),
            details = new { legalName = "Emisora SAC", fiscalAddress = "Av. Larco 123", ubigeo = "150122" },
        });
        Assert.Equal(HttpStatusCode.Created, companyResponse.StatusCode);
        var company = (await companyResponse.Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!;

        return new Setup(tenantId, owner, company.Id, await CreateSeriesAsync(owner, company.Id, "01", "F001"), await CreateSeriesAsync(owner, company.Id, "03", "B001"));
    }

    private static async Task<SeriesDto> CreateSeriesAsync(HttpClient client, Guid companyId, string type, string code)
    {
        var response = await client.PostAsJsonAsync("/api/v1/series", new { companyId, documentTypeCode = type, code });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;
    }

    private static object InvoiceBody(Guid seriesId, decimal quantity = 2m, decimal unitValue = 100m, DateOnly? issueDate = null, string buyerType = "6", string? buyerNumber = null, string currency = "PEN") => new
    {
        seriesId,
        issueDate = (issueDate ?? TodayInLima()).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        currency,
        buyer = new { documentTypeCode = buyerType, documentNumber = buyerNumber ?? "20100066603", name = "Cliente SAC" },
        lines = new[]
        {
            new { description = "Servicio de consultoría", unitCode = "ZZ", tax = new { quantity, unitValue, igvAffectationCode = "10" } },
        },
        rates = new { igvRate = 0.18m, ivapRate = 0.04m, icbperUnitAmount = 0.5m },
    };

    private static async Task<HttpResponseMessage> PostDocumentAsync(HttpClient client, object body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    private static async Task<DocumentDto> ReadDocumentAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;

    // ---------- series ----------

    [Theory]
    [InlineData("01", "B001")]
    [InlineData("01", "0001")]
    [InlineData("01", "F01")]
    [InlineData("03", "F001")]
    [InlineData("07", "X001")]
    [InlineData("99", "F001")]
    public async Task Series_codes_must_match_the_document_type(string type, string code)
    {
        var setup = await NewTenantWithSeriesAsync($"Series Format {type}{code} SAC");

        var response = await setup.Owner.PostAsJsonAsync("/api/v1/series", new { companyId = setup.CompanyId, documentTypeCode = type, code });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_series_are_rejected_but_notes_may_use_f_or_b()
    {
        var setup = await NewTenantWithSeriesAsync("Series Dup SAC");

        var duplicate = await setup.Owner.PostAsJsonAsync("/api/v1/series", new { companyId = setup.CompanyId, documentTypeCode = "01", code = "f001" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("SF-BIL-003", await ProblemCodeAsync(duplicate));

        await CreateSeriesAsync(setup.Owner, setup.CompanyId, "07", "FC01");
        await CreateSeriesAsync(setup.Owner, setup.CompanyId, "07", "BC01");
    }

    // ---------- document creation and numbering ----------

    [Fact]
    public async Task An_invoice_is_calculated_numbered_and_stored()
    {
        var setup = await NewTenantWithSeriesAsync("Doc Create SAC");

        var response = await PostDocumentAsync(setup.Owner, InvoiceBody(setup.InvoiceSeries.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var document = await ReadDocumentAsync(response);
        Assert.Equal("F001", document.Series);
        Assert.Equal(1, document.Number);
        Assert.Equal("F001-1", document.FullNumber);
        Assert.Equal(DocumentStatus.Validated, document.Status);
        Assert.Equal(236.00m, document.Totals.PayableAmount);
        Assert.Equal(36.00m, document.Totals.TotalIgv);
        Assert.Equal(200.00m, Assert.Single(document.Lines).LineExtensionAmount);

        var second = await ReadDocumentAsync(await PostDocumentAsync(setup.Owner, InvoiceBody(setup.InvoiceSeries.Id, quantity: 1m)));
        Assert.Equal(2, second.Number);

        var fetched = (await setup.Owner.GetFromJsonAsync<DocumentDto>($"/api/v1/documents/{document.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal(document.Totals.PayableAmount, fetched.Totals.PayableAmount);
        var list = (await setup.Owner.GetFromJsonAsync<List<DocumentDto>>($"/api/v1/documents?companyId={setup.CompanyId}", ApiFixture.JsonOptions))!;
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task Receipts_use_their_own_series_counter()
    {
        var setup = await NewTenantWithSeriesAsync("Doc Receipt SAC");
        await PostDocumentAsync(setup.Owner, InvoiceBody(setup.InvoiceSeries.Id));

        var receipt = await ReadDocumentAsync(await PostDocumentAsync(
            setup.Owner, InvoiceBody(setup.ReceiptSeries.Id, buyerType: "1", buyerNumber: "12345678")));

        Assert.Equal("B001", receipt.Series);
        Assert.Equal(1, receipt.Number);
    }

    [Fact]
    public async Task Concurrent_requests_never_duplicate_or_skip_numbers()
    {
        var setup = await NewTenantWithSeriesAsync("Doc Concurrency SAC");

        var responses = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => PostDocumentAsync(setup.Owner, InvoiceBody(setup.InvoiceSeries.Id, quantity: 1m))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var numbers = (await Task.WhenAll(responses.Select(ReadDocumentAsync))).Select(d => d.Number).Order().ToList();
        Assert.Equal(Enumerable.Range(1, 40).Select(i => (long)i), numbers);

        var last = await api.Postgres.ScalarAsOwnerAsync<long>($"SELECT last_number FROM billing.series WHERE id = '{setup.InvoiceSeries.Id}'");
        Assert.Equal(40, last);
    }

    // ---------- idempotency ----------

    [Fact]
    public async Task The_same_key_and_content_returns_the_original_document_without_consuming_a_number()
    {
        var setup = await NewTenantWithSeriesAsync("Doc Idem SAC");
        var key = Guid.NewGuid().ToString("N");
        var body = InvoiceBody(setup.InvoiceSeries.Id);

        var first = await ReadDocumentAsync(await PostDocumentAsync(setup.Owner, body, key));
        var again = await PostDocumentAsync(setup.Owner, body, key);

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal(first.Id, (await ReadDocumentAsync(again)).Id);
        var next = await ReadDocumentAsync(await PostDocumentAsync(setup.Owner, body));
        Assert.Equal(2, next.Number);
    }

    [Fact]
    public async Task The_same_key_with_different_content_is_a_conflict()
    {
        var setup = await NewTenantWithSeriesAsync("Doc Idem Conflict SAC");
        var key = Guid.NewGuid().ToString("N");
        await PostDocumentAsync(setup.Owner, InvoiceBody(setup.InvoiceSeries.Id), key);

        var response = await PostDocumentAsync(setup.Owner, InvoiceBody(setup.InvoiceSeries.Id, quantity: 5m), key);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("SF-BIL-008", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Concurrent_retries_with_one_key_create_exactly_one_document()
    {
        var setup = await NewTenantWithSeriesAsync("Doc Idem Race SAC");
        var key = Guid.NewGuid().ToString("N");
        var body = InvoiceBody(setup.InvoiceSeries.Id);

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => PostDocumentAsync(setup.Owner, body, key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = (await Task.WhenAll(responses.Select(ReadDocumentAsync))).Select(d => d.Id).Distinct().ToList();
        Assert.Single(ids);
        var count = await api.Postgres.ScalarAsOwnerAsync<long>($"SELECT count(*) FROM billing.document WHERE series_id = '{setup.InvoiceSeries.Id}'");
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has spaces in the key")]
    public async Task A_missing_or_malformed_idempotency_key_is_rejected(string key)
    {
        var setup = await NewTenantWithSeriesAsync("Doc Key SAC");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents") { Content = JsonContent.Create(InvoiceBody(setup.InvoiceSeries.Id)) };
        if (key.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        var response = await setup.Owner.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("SF-VAL-005", await ProblemCodeAsync(response));
    }

    // ---------- validation ----------

    [Fact]
    public async Task Invoices_require_a_valid_ruc_buyer()
    {
        var setup = await NewTenantWithSeriesAsync("Doc Buyer SAC");

        var dni = await PostDocumentAsync(setup.Owner, InvoiceBody(setup.InvoiceSeries.Id, buyerType: "1", buyerNumber: "12345678"));
        var badRuc = await PostDocumentAsync(setup.Owner, InvoiceBody(setup.InvoiceSeries.Id, buyerNumber: "20100066604"));

        Assert.Equal("SF-BIL-006", await ProblemCodeAsync(dni));
        Assert.Equal("SF-BIL-006", await ProblemCodeAsync(badRuc));
    }

    [Fact]
    public async Task Issue_dates_must_be_today_or_within_the_allowed_window()
    {
        var setup = await NewTenantWithSeriesAsync("Doc Dates SAC");
        var today = TodayInLima();

        Assert.Equal(HttpStatusCode.Created, (await PostDocumentAsync(setup.Owner, InvoiceBody(setup.InvoiceSeries.Id, issueDate: today.AddDays(-3)))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostDocumentAsync(setup.Owner, InvoiceBody(setup.InvoiceSeries.Id, issueDate: today.AddDays(-4)))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostDocumentAsync(setup.Owner, InvoiceBody(setup.InvoiceSeries.Id, issueDate: today.AddDays(1)))).StatusCode);
    }

    [Fact]
    public async Task Invalid_tax_input_is_reported_with_tax_error_codes_and_consumes_no_number()
    {
        var setup = await NewTenantWithSeriesAsync("Doc Tax SAC");

        var response = await PostDocumentAsync(setup.Owner, InvoiceBody(setup.InvoiceSeries.Id, quantity: -1m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("SF-TAX-001", await ProblemCodeAsync(response));
        var last = await api.Postgres.ScalarAsOwnerAsync<long>($"SELECT last_number FROM billing.series WHERE id = '{setup.InvoiceSeries.Id}'");
        Assert.Equal(0, last);
    }

    [Fact]
    public async Task Currency_and_lines_are_validated()
    {
        var setup = await NewTenantWithSeriesAsync("Doc Basics SAC");

        Assert.Equal("SF-BIL-006", await ProblemCodeAsync(await PostDocumentAsync(setup.Owner, InvoiceBody(setup.InvoiceSeries.Id, currency: "soles"))));

        var noLines = new
        {
            seriesId = setup.InvoiceSeries.Id,
            issueDate = TodayInLima().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            currency = "PEN",
            buyer = new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
            lines = Array.Empty<object>(),
            rates = new { igvRate = 0.18m },
        };
        Assert.Equal("SF-BIL-006", await ProblemCodeAsync(await PostDocumentAsync(setup.Owner, noLines)));
    }

    [Fact]
    public async Task Inactive_series_stop_issuing_but_replays_still_work()
    {
        var setup = await NewTenantWithSeriesAsync("Doc Inactive SAC");
        var key = Guid.NewGuid().ToString("N");
        var original = await ReadDocumentAsync(await PostDocumentAsync(setup.Owner, InvoiceBody(setup.InvoiceSeries.Id), key));

        Assert.Equal(HttpStatusCode.NoContent, (await setup.Owner.PostAsync($"/api/v1/series/{setup.InvoiceSeries.Id}/deactivate", null)).StatusCode);

        var fresh = await PostDocumentAsync(setup.Owner, InvoiceBody(setup.InvoiceSeries.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fresh.StatusCode);
        Assert.Equal("SF-BIL-004", await ProblemCodeAsync(fresh));

        var replay = await PostDocumentAsync(setup.Owner, InvoiceBody(setup.InvoiceSeries.Id), key);
        Assert.Equal(original.Id, (await ReadDocumentAsync(replay)).Id);
    }

    // ---------- isolation, immutability, RBAC ----------

    [Fact]
    public async Task Tenants_cannot_see_or_use_each_others_series_and_documents()
    {
        var a = await NewTenantWithSeriesAsync("Doc Iso A SAC");
        var b = await NewTenantWithSeriesAsync("Doc Iso B SAC");
        var documentOfA = await ReadDocumentAsync(await PostDocumentAsync(a.Owner, InvoiceBody(a.InvoiceSeries.Id)));

        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/documents/{documentOfA.Id}")).StatusCode);
        var listB = (await b.Owner.GetFromJsonAsync<List<DocumentDto>>("/api/v1/documents", ApiFixture.JsonOptions))!;
        Assert.DoesNotContain(listB, d => d.Id == documentOfA.Id);

        var useForeignSeries = await PostDocumentAsync(b.Owner, InvoiceBody(a.InvoiceSeries.Id));
        Assert.Equal(HttpStatusCode.NotFound, useForeignSeries.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PostAsync($"/api/v1/series/{a.InvoiceSeries.Id}/deactivate", null)).StatusCode);

        // The same idempotency key in two tenants refers to two different documents.
        var key = Guid.NewGuid().ToString("N");
        var docA = await ReadDocumentAsync(await PostDocumentAsync(a.Owner, InvoiceBody(a.InvoiceSeries.Id), key));
        var docB = await ReadDocumentAsync(await PostDocumentAsync(b.Owner, InvoiceBody(b.InvoiceSeries.Id), key));
        Assert.NotEqual(docA.Id, docB.Id);
    }

    [Fact]
    public async Task Issued_documents_cannot_be_updated_or_deleted_even_by_the_runtime_role()
    {
        var setup = await NewTenantWithSeriesAsync("Doc Immutable SAC");
        var document = await ReadDocumentAsync(await PostDocumentAsync(setup.Owner, InvoiceBody(setup.InvoiceSeries.Id)));

        await using var connection = new NpgsqlConnection(api.Postgres.AppConnectionString);
        await connection.OpenAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
        {
            scope.Parameters.AddWithValue("t", setup.TenantId.ToString("D"));
            await scope.ExecuteNonQueryAsync();
        }

        foreach (var sql in new[]
        {
            $"UPDATE billing.document SET payable_amount = 1 WHERE id = '{document.Id}'",
            $"DELETE FROM billing.document WHERE id = '{document.Id}'",
            $"UPDATE billing.document_line SET line_extension_amount = 1 WHERE document_id = '{document.Id}'",
            $"DELETE FROM billing.idempotency_key WHERE document_id = '{document.Id}'",
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        // The counter cannot be rewound below the numbers already issued.
        await using var rewind = new NpgsqlCommand($"UPDATE billing.series SET last_number = -1 WHERE id = '{setup.InvoiceSeries.Id}'", connection);
        var violation = await Assert.ThrowsAsync<PostgresException>(() => rewind.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, violation.SqlState);
    }

    [Fact]
    public async Task Roles_control_who_can_manage_series_and_issue_documents()
    {
        var setup = await NewTenantWithSeriesAsync("Doc Rbac SAC");
        var sales = await ApiFixture.CreateUserAsync(setup.Owner, Roles.Sales, setup.TenantId);
        var reader = await ApiFixture.CreateUserAsync(setup.Owner, Roles.ReadOnly, setup.TenantId);
        using var salesClient = api.ClientFor(await api.LoginOkAsync(sales.Email, sales.Password));
        using var readerClient = api.ClientFor(await api.LoginOkAsync(reader.Email, reader.Password));

        Assert.Equal(HttpStatusCode.Created, (await PostDocumentAsync(salesClient, InvoiceBody(setup.InvoiceSeries.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await salesClient.PostAsJsonAsync("/api/v1/series", new { companyId = setup.CompanyId, documentTypeCode = "01", code = "F002" })).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await PostDocumentAsync(readerClient, InvoiceBody(setup.InvoiceSeries.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readerClient.GetAsync("/api/v1/documents")).StatusCode);
    }
}
