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
using SecureFact.SharedKernel.Domain;

namespace SecureFact.Security.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class CpePipelineApiTests(ApiFixture api)
{
    private const string SolPassword = "Sol-Clave-pipeline-secret-7";
    private static int _rucCounter = 9_000_000;

    private sealed record Setup(Guid TenantId, HttpClient Owner, CompanyDto Company, SeriesDto InvoiceSeries, SeriesDto ReceiptSeries, string OwnerEmail = "");

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

    private async Task<Setup> NewTenantAsync(string name, bool certificate = true, bool solCredentials = true)
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

        if (solCredentials)
        {
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = SolPassword })).StatusCode);
        }

        async Task<SeriesDto> SeriesAsync(string type, string code) =>
            (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = type, code })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;

        return new Setup(tenantId, owner, company, await SeriesAsync("01", "F001"), await SeriesAsync("03", "B001"), user.Email);
    }

    private static async Task<DocumentDto> NewDocumentAsync(HttpClient client, SeriesDto series, bool receipt = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
        {
            Content = JsonContent.Create(new
            {
                seriesId = series.Id,
                issueDate = TodayInLima().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
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

    private static async Task<ElectronicDocumentDto> PrepareAsync(HttpClient client, Guid documentId)
    {
        var response = await client.PostAsync($"/api/v1/documents/{documentId}/electronic", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
    }

    private static async Task<ElectronicDocumentDto> SendOkAsync(HttpClient client, Guid id)
    {
        var response = await client.PostAsync($"/api/v1/electronic-documents/{id}/send", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
    }

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private async Task<(Setup Setup, DocumentDto Document, ElectronicDocumentDto Electronic)> ReadyAsync(string name)
    {
        var setup = await NewTenantAsync(name);
        var document = await NewDocumentAsync(setup.Owner, setup.InvoiceSeries);
        return (setup, document, await PrepareAsync(setup.Owner, document.Id));
    }

    private static string Reference(DocumentDto d) => $"{d.Series}-{d.Number}";

    // ---------- prepare ----------

    [Fact]
    public async Task A_document_is_prepared_signed_and_the_xml_is_kept_unchanged()
    {
        var (setup, document, electronic) = await ReadyAsync("Cpe Prepare SAC");

        Assert.Equal(EDocumentState.ReadyToSend, electronic.State);
        Assert.Equal(0, electronic.Attempts);
        Assert.Equal($"{setup.Company.Ruc}-01-F001-1", electronic.FileBaseName);

        var xml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml");
        Assert.Contains("<cbc:ID>F001-1</cbc:ID>", xml, StringComparison.Ordinal);
        var inspection = new XmlDsigSigner().Verify(xml).Value;
        Assert.True(inspection.IsValid);
        Assert.Equal(electronic.DigestValue, inspection.DigestValue);
        Assert.Contains(setup.Company.Ruc, inspection.CertificateSubject, StringComparison.Ordinal);

        var again = await PrepareAsync(setup.Owner, document.Id);
        Assert.Equal(electronic.Id, again.Id);
        Assert.Equal(xml, await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{again.Id}/xml"));
        Assert.Equal(electronic.Id, (await setup.Owner.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/documents/{document.Id}/electronic", ApiFixture.JsonOptions))!.Id);
    }

    [Fact]
    public async Task Receipts_are_prepared_too()
    {
        var setup = await NewTenantAsync("Cpe Receipt SAC");
        var receipt = await NewDocumentAsync(setup.Owner, setup.ReceiptSeries, receipt: true);

        var electronic = await PrepareAsync(setup.Owner, receipt.Id);

        Assert.Equal("03", electronic.DocumentTypeCode);
        Assert.Equal($"{setup.Company.Ruc}-03-B001-1", electronic.FileBaseName);
    }

    [Fact]
    public async Task Preparing_without_an_active_certificate_fails_cleanly_and_stores_nothing()
    {
        var setup = await NewTenantAsync("Cpe NoCert SAC", certificate: false);
        var document = await NewDocumentAsync(setup.Owner, setup.InvoiceSeries);

        var response = await setup.Owner.PostAsync($"/api/v1/documents/{document.Id}/electronic", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("SF-CRT-004", await ProblemCodeAsync(response));
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Owner.GetAsync($"/api/v1/documents/{document.Id}/electronic")).StatusCode);
    }

    // ---------- sending ----------

    [Fact]
    public async Task An_accepted_invoice_records_the_cdr_history_and_audit_and_is_idempotent()
    {
        var (setup, document, electronic) = await ReadyAsync("Cpe Accept SAC");
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, Reference(document))));

        var sent = await SendOkAsync(setup.Owner, electronic.Id);

        Assert.Equal(EDocumentState.Accepted, sent.State);
        Assert.Equal(1, sent.Attempts);
        Assert.Equal(0, sent.CdrResponseCode);
        Assert.Equal("201200000230061", sent.CdrProcessId);
        Assert.NotNull(sent.ProcessedAt);

        // What went out: SOL user = RUC + user, a ZIP named after the package holding exactly the stored signed XML.
        var call = Assert.Single(api.Sunat.Calls);
        Assert.Equal(setup.Company.Ruc + "MODDATOS", call.Credentials.UserName);
        Assert.Equal(SolPassword, call.Credentials.SolPassword);
        Assert.Equal(electronic.FileBaseName + ".zip", call.ZipFileName);
        var (entry, content) = new ZipCpePackager().Unzip(call.Zip).Value;
        Assert.Equal(electronic.FileBaseName + ".xml", entry);
        Assert.Equal(await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronic.Id}/xml"), content);

        var events = (await setup.Owner.GetFromJsonAsync<List<ElectronicDocumentEventDto>>($"/api/v1/electronic-documents/{electronic.Id}/events", ApiFixture.JsonOptions))!;
        Assert.Equal(
            [EDocumentState.ReadyToSend, EDocumentState.Sending, EDocumentState.Accepted],
            events.Select(e => e.To).ToArray());

        var cdr = await setup.Owner.GetAsync($"/api/v1/electronic-documents/{electronic.Id}/cdr");
        Assert.Equal("application/zip", cdr.Content.Headers.ContentType!.MediaType);
        Assert.True(new ZipCpePackager().Unzip(await cdr.Content.ReadAsByteArrayAsync()).IsSuccess);

        var again = await SendOkAsync(setup.Owner, electronic.Id);
        Assert.Equal(EDocumentState.Accepted, again.State);
        Assert.Single(api.Sunat.Calls);

        var audit = await setup.Owner.GetStringAsync("/api/v1/audit?action=cpe.electronic_document.processed&take=20");
        Assert.Contains(electronic.Id.ToString("D"), audit, StringComparison.Ordinal);
        Assert.DoesNotContain(SolPassword, audit + string.Join(Environment.NewLine, api.Logs.Snapshot()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Observations_are_returned_and_the_invoice_is_accepted_with_observations()
    {
        var (setup, document, electronic) = await ReadyAsync("Cpe Observed SAC");
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, Reference(document), "0", "aceptada", "4031 - Debe indicar el nombre comercial")));

        var sent = await SendOkAsync(setup.Owner, electronic.Id);

        Assert.Equal(EDocumentState.AcceptedWithObservations, sent.State);
        Assert.Equal(new CdrObservation("4031", "Debe indicar el nombre comercial"), Assert.Single(sent.CdrObservations));
    }

    [Fact]
    public async Task The_owners_are_told_when_sunat_rejects_a_document_with_the_answer_of_sunat_and_not_when_it_accepts_it()
    {
        var (setup, document, electronic) = await ReadyAsync("Cpe Reject Notice SAC");
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, Reference(document), "2047", "Es obligatorio al menos un AdditionalMonetaryTotal")));

        Assert.Equal(EDocumentState.Rejected, (await SendOkAsync(setup.Owner, electronic.Id)).State);

        await api.DrainMailAsync();
        var notice = Assert.Single(api.Mail.To(setup.OwnerEmail), m => m.Subject.Contains("rechazó", StringComparison.Ordinal));
        Assert.Contains($"F001-{document.Number}", notice.Subject, StringComparison.Ordinal);
        Assert.Contains("código 2047", notice.Text, StringComparison.Ordinal);
        Assert.Contains("AdditionalMonetaryTotal", notice.Text, StringComparison.Ordinal);

        var (accepted, acceptedDocument, acceptedElectronic) = await ReadyAsync("Cpe Accept Notice SAC");
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(accepted.Company.Ruc, Reference(acceptedDocument))));
        Assert.Equal(EDocumentState.Accepted, (await SendOkAsync(accepted.Owner, acceptedElectronic.Id)).State);
        await api.DrainMailAsync();
        Assert.DoesNotContain(api.Mail.To(accepted.OwnerEmail), m => m.Subject.Contains("comprobante", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rejected_invoice_is_final_and_immutable_even_for_the_database_owner()
    {
        var (setup, document, electronic) = await ReadyAsync("Cpe Reject SAC");
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, Reference(document), "2047", "Es obligatorio al menos un AdditionalMonetaryTotal")));

        var sent = await SendOkAsync(setup.Owner, electronic.Id);

        Assert.Equal(EDocumentState.Rejected, sent.State);
        Assert.Equal(2047, sent.CdrResponseCode);
        var retry = await setup.Owner.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal("SF-CPE-004", await ProblemCodeAsync(retry));

        await using var connection = new NpgsqlConnection(api.Postgres.OwnerConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[]
        {
            $"UPDATE cpe.electronic_document SET state = 'Accepted' WHERE id = '{electronic.Id}'",
            $"UPDATE cpe.electronic_document SET attempts = 0 WHERE id = '{electronic.Id}'",
            $"DELETE FROM cpe.electronic_document WHERE id = '{electronic.Id}'",
            "UPDATE cpe.electronic_document_event SET detail = 'tampered'",
            "DELETE FROM cpe.electronic_document_event",
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal("42501", failure.SqlState);
        }
    }

    [Fact]
    public async Task The_signed_xml_cannot_be_changed_while_the_document_is_still_open()
    {
        var (_, _, electronic) = await ReadyAsync("Cpe Frozen SAC");

        await using var connection = new NpgsqlConnection(api.Postgres.OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"UPDATE cpe.electronic_document SET signed_xml = '<x/>' WHERE id = '{electronic.Id}'", connection);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal("42501", failure.SqlState);
    }

    [Fact]
    public async Task A_transient_failure_queues_a_retry_with_backoff_and_a_later_send_succeeds()
    {
        var (setup, document, electronic) = await ReadyAsync("Cpe Transient SAC");
        api.Sunat.Enqueue(ChannelReply.Down("timeout"));

        var first = await SendOkAsync(setup.Owner, electronic.Id);

        Assert.Equal(EDocumentState.ReadyToSend, first.State);
        Assert.Equal(1, first.Attempts);
        Assert.NotNull(first.NextAttemptAt);
        Assert.True(first.NextAttemptAt > DateTimeOffset.UtcNow);

        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, Reference(document))));
        var second = await SendOkAsync(setup.Owner, electronic.Id);

        Assert.Equal(EDocumentState.Accepted, second.State);
        Assert.Equal(2, second.Attempts);
        Assert.Null(second.NextAttemptAt);
    }

    [Fact]
    public async Task A_permanent_fault_stops_sending_until_an_operator_retries()
    {
        var (setup, document, electronic) = await ReadyAsync("Cpe Permanent SAC");
        api.Sunat.Enqueue(ChannelReply.Failed(new SunatFault(SunatSide.Client, 1033, "formato inválido", Retryable: false)));

        var failed = await SendOkAsync(setup.Owner, electronic.Id);

        Assert.Equal(EDocumentState.Failed, failed.State);
        Assert.Equal("1033", failed.LastErrorCode);
        var blocked = await setup.Owner.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/send", null);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Single(api.Sunat.Calls);

        var retried = (await (await setup.Owner.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/retry", null)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(EDocumentState.ReadyToSend, retried.State);
        Assert.Equal(0, retried.Attempts);

        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, Reference(document))));
        Assert.Equal(EDocumentState.Accepted, (await SendOkAsync(setup.Owner, electronic.Id)).State);
    }

    [Fact]
    public async Task Repeated_transient_failures_end_in_failed_after_the_attempt_limit()
    {
        var (setup, _, electronic) = await ReadyAsync("Cpe Exhausted SAC");

        ElectronicDocumentDto last = electronic;
        for (var i = 0; i < new EDocumentStateMachine().MaxAttempts; i++)
        {
            api.Sunat.Enqueue(ChannelReply.Down("sunat down"));
            last = await SendOkAsync(setup.Owner, electronic.Id);
        }

        Assert.Equal(EDocumentState.Failed, last.State);
        Assert.Equal(new EDocumentStateMachine().MaxAttempts, last.Attempts);
        Assert.Null(last.NextAttemptAt);
    }

    [Fact]
    public async Task A_cdr_for_another_document_or_taxpayer_is_never_accepted()
    {
        var (setup, document, electronic) = await ReadyAsync("Cpe Mismatch SAC");

        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, "F001-999")));
        var wrongDocument = await SendOkAsync(setup.Owner, electronic.Id);
        Assert.Equal(EDocumentState.Failed, wrongDocument.State);
        Assert.Equal("SF-CPE-006", wrongDocument.LastErrorCode);
        Assert.Null(wrongDocument.CdrResponseCode);

        await setup.Owner.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/retry", null);
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(NewRuc(), Reference(document))));
        var wrongTaxpayer = await SendOkAsync(setup.Owner, electronic.Id);
        Assert.Equal(EDocumentState.Failed, wrongTaxpayer.State);
        Assert.Equal("SF-CPE-006", wrongTaxpayer.LastErrorCode);

        // The bytes are kept for investigation.
        Assert.Equal(HttpStatusCode.OK, (await setup.Owner.GetAsync($"/api/v1/electronic-documents/{electronic.Id}/cdr")).StatusCode);
    }

    [Fact]
    public async Task An_unreadable_cdr_leaves_the_document_failed_not_accepted()
    {
        var (setup, _, electronic) = await ReadyAsync("Cpe Garbled SAC");
        api.Sunat.Enqueue(ChannelReply.Cdr(new ZipCpePackager().Zip("R-garbage-file", "<not-a-cdr/>").Value));

        var sent = await SendOkAsync(setup.Owner, electronic.Id);

        Assert.Equal(EDocumentState.Failed, sent.State);
        Assert.Null(sent.CdrResponseCode);
    }

    [Fact]
    public async Task Receipts_are_not_sent_one_by_one_and_missing_credentials_do_not_burn_an_attempt()
    {
        var setup = await NewTenantAsync("Cpe Guards SAC", solCredentials: false);
        var receipt = await PrepareAsync(setup.Owner, (await NewDocumentAsync(setup.Owner, setup.ReceiptSeries, receipt: true)).Id);
        var invoice = await PrepareAsync(setup.Owner, (await NewDocumentAsync(setup.Owner, setup.InvoiceSeries)).Id);

        var receiptSend = await setup.Owner.PostAsync($"/api/v1/electronic-documents/{receipt.Id}/send", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, receiptSend.StatusCode);
        Assert.Equal("SF-CPE-002", await ProblemCodeAsync(receiptSend));

        var noCredentials = await setup.Owner.PostAsync($"/api/v1/electronic-documents/{invoice.Id}/send", null);
        Assert.Equal(HttpStatusCode.Conflict, noCredentials.StatusCode);
        Assert.Equal("SF-CRT-004", await ProblemCodeAsync(noCredentials));

        Assert.Empty(api.Sunat.Calls);
        var current = (await setup.Owner.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/electronic-documents/{invoice.Id}", ApiFixture.JsonOptions))!;
        Assert.Equal(EDocumentState.ReadyToSend, current.State);
        Assert.Equal(0, current.Attempts);
    }

    [Fact]
    public async Task Two_simultaneous_sends_reach_sunat_only_once()
    {
        var (setup, document, electronic) = await ReadyAsync("Cpe Race SAC");
        api.Sunat.Enqueue(call =>
        {
            Thread.Sleep(400);
            return ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, Reference(document)));
        });
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, Reference(document))));

        var responses = await Task.WhenAll(
            setup.Owner.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/send", null),
            setup.Owner.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/send", null));

        Assert.Single(api.Sunat.Calls);
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        Assert.Equal(EDocumentState.Accepted, (await setup.Owner.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/electronic-documents/{electronic.Id}", ApiFixture.JsonOptions))!.State);
    }

    // ---------- printed representation ----------

    private static string PdfText(byte[] pdf)
    {
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

        return content.ToString();
    }

    [Fact]
    public async Task An_invoice_prints_with_the_numbers_billing_issued_and_the_digest_of_its_signed_xml()
    {
        var (setup, document, electronic) = await ReadyAsync("Cpe Pdf SAC");

        var response = await setup.Owner.GetAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        var pdf = await response.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("%PDF-1.4", System.Text.Encoding.Latin1.GetString(pdf[..8]), StringComparison.Ordinal);
        var content = PdfText(pdf);
        foreach (var expected in new[]
        {
            "FACTURA ELECTRÓNICA", $"RUC {setup.Company.Ruc}", $"{document.Series}-{document.Number}", "(Emisora SAC)", "(Cliente SAC)", "RUC:", "20100066603",
            "S/ 200.00", "S/ 36.00", "S/ 236.00", "SON: DOSCIENTOS TREINTA Y SEIS CON 00/100 SOLES", "Representación impresa de la factura electrónica", electronic.DigestValue,
        })
        {
            Assert.Contains(expected, content, StringComparison.Ordinal);
        }

        // The PDF is a view: it is the same every time and it never moves the electronic document.
        Assert.Equal(pdf, await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf"));
        Assert.Equal(EDocumentState.ReadyToSend, (await setup.Owner.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/electronic-documents/{electronic.Id}", ApiFixture.JsonOptions))!.State);
    }

    [Fact]
    public async Task A_receipt_prints_with_its_own_denomination_and_the_buyer_type_by_name()
    {
        var setup = await NewTenantAsync("Cpe Pdf Receipt SAC");
        var receipt = await PrepareAsync(setup.Owner, (await NewDocumentAsync(setup.Owner, setup.ReceiptSeries, receipt: true)).Id);

        var content = PdfText(await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{receipt.Id}/pdf"));

        Assert.Contains("BOLETA DE VENTA ELECTRÓNICA", content, StringComparison.Ordinal);
        Assert.Contains("Representación impresa de la boleta de venta electrónica", content, StringComparison.Ordinal);
        Assert.Contains("DNI:", content, StringComparison.Ordinal);
        Assert.Contains("12345678", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_pdf_is_isolated_guarded_and_not_offered_for_summaries()
    {
        var (a, _, electronic) = await ReadyAsync("Cpe Pdf Iso A SAC");
        var b = await NewTenantAsync("Cpe Pdf Iso B SAC");
        var sales = await ApiFixture.CreateUserAsync(a.Owner, Roles.Sales, a.TenantId);
        using var salesClient = api.ClientFor(await api.LoginOkAsync(sales.Email, sales.Password));
        using var anonymous = api.NewClient();

        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await salesClient.GetAsync($"/api/v1/electronic-documents/{electronic.Id}/pdf")).StatusCode); // reading needs documents.read only

        // A daily summary has no printed representation.
        await NewDocumentAsync(a.Owner, a.ReceiptSeries, receipt: true);
        var summary = await a.Owner.PostAsJsonAsync("/api/v1/summaries", new { companyId = a.Company.Id, referenceDate = TodayInLima().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
        Assert.Equal(HttpStatusCode.Created, summary.StatusCode);
        var created = (await summary.Content.ReadFromJsonAsync<List<SummaryDto>>(ApiFixture.JsonOptions))!;
        var refused = await a.Owner.GetAsync($"/api/v1/electronic-documents/{created[0].Document.Id}/pdf");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("SF-CPE-002", await ProblemCodeAsync(refused));
    }

    // ---------- security ----------

    [Fact]
    public async Task Electronic_documents_are_isolated_between_tenants()
    {
        var (a, document, electronic) = await ReadyAsync("Cpe Iso A SAC");
        var b = await NewTenantAsync("Cpe Iso B SAC");

        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PostAsync($"/api/v1/documents/{document.Id}/electronic", null)).StatusCode);
        foreach (var path in new[] { "", "/events", "/xml", "/cdr" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/electronic-documents/{electronic.Id}{path}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/send", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/retry", null)).StatusCode);
        Assert.Empty(api.Sunat.Calls);
        Assert.Equal(EDocumentState.ReadyToSend, (await a.Owner.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/electronic-documents/{electronic.Id}", ApiFixture.JsonOptions))!.State);
    }

    [Fact]
    public async Task Roles_control_who_can_prepare_and_send()
    {
        var (setup, document, electronic) = await ReadyAsync("Cpe Rbac SAC");

        async Task<HttpClient> ClientAsync(string role)
        {
            var user = await ApiFixture.CreateUserAsync(setup.Owner, role, setup.TenantId);
            return api.ClientFor(await api.LoginOkAsync(user.Email, user.Password));
        }

        using var sales = await ClientAsync(Roles.Sales);
        using var auditor = await ClientAsync(Roles.Auditor);
        using var billing = await ClientAsync(Roles.BillingAdmin);
        using var anonymous = api.NewClient();

        foreach (var client in new[] { sales, auditor })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/electronic-documents/{electronic.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/send", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/retry", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/v1/documents/{document.Id}/electronic", null)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/v1/electronic-documents/{electronic.Id}/send", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/electronic-documents/{electronic.Id}/xml")).StatusCode);
        Assert.Empty(api.Sunat.Calls);

        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Company.Ruc, Reference(document))));
        Assert.Equal(EDocumentState.Accepted, (await SendOkAsync(billing, electronic.Id)).State);
    }
}
