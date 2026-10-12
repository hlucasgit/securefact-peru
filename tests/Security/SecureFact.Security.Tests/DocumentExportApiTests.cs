using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SecureFact.Audit.Contracts;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>A period of the documents of a company in one ZIP (ADR-071): the signed XML and the CDR as they are kept, an index, the limits and the isolation of the tenant.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class DocumentExportApiTests(ApiFixture api)
{
    private static int _rucCounter = 50_000_000;

    private sealed record Setup(Guid TenantId, HttpClient Owner, CompanyDto Company, SeriesDto Invoice, string Ruc);

    private sealed record AuditRow(string Action, JsonElement? NewValues);

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
        var series = (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.Id, documentTypeCode = "01", code = "F001" })).Content.ReadFromJsonAsync<SeriesDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = "Sol-Clave-exp-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/certificates", new { companyId = company.Id, pfxBase64 = PfxFor(ruc), password = "pw" })).StatusCode);
        return new Setup(tenantId, owner, company, series, ruc);
    }

    private static async Task<DocumentDto> IssueAsync(Setup setup, string buyerName = "Cliente SAC")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
        {
            Content = JsonContent.Create(new
            {
                seriesId = setup.Invoice.Id,
                issueDate = Iso(TodayInLima()),
                currency = "PEN",
                buyer = new { documentTypeCode = "6", documentNumber = "20100066603", name = buyerName },
                lines = new[] { new { description = "Servicio de consultoría", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10" } } },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await setup.Owner.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
    }

    private static async Task<Guid> PrepareAsync(Setup setup, Guid documentId)
    {
        var prepared = await setup.Owner.PostAsync($"/api/v1/documents/{documentId}/electronic", null);
        Assert.True(prepared.IsSuccessStatusCode);
        return (await setup.Owner.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/documents/{documentId}/electronic", ApiFixture.JsonOptions))!.Id;
    }

    private async Task AcceptAsync(Setup setup, DocumentDto document, Guid electronicId)
    {
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Ruc, $"{document.Series}-{document.Number}")));
        var sent = (await (await setup.Owner.PostAsync($"/api/v1/electronic-documents/{electronicId}/send", null)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(EDocumentState.Accepted, sent.State);
    }

    private static string Path(Guid companyId, DateOnly from, DateOnly to, string? type = null) =>
        $"/api/v1/electronic-documents/export?companyId={companyId}&from={Iso(from)}&to={Iso(to)}{(type is null ? string.Empty : $"&documentTypeCode={type}")}";

    private static async Task<(List<string> Entries, string Index)> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        var index = zip.GetEntry("indice.csv")!;
        using var reader = new StreamReader(index.Open(), Encoding.UTF8);
        return (zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToList(), await reader.ReadToEndAsync());
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString();
    }

    [Fact]
    public async Task The_period_comes_in_one_zip_with_each_signed_xml_the_cdr_of_the_accepted_and_an_index()
    {
        var setup = await NewTenantAsync("Export SAC");
        var accepted = await IssueAsync(setup, "Cliente \"Comillas\", SAC");
        var acceptedId = await PrepareAsync(setup, accepted.Id);
        await AcceptAsync(setup, accepted, acceptedId);
        var pending = await IssueAsync(setup, "=SUMA(1+1) SAC");
        await PrepareAsync(setup, pending.Id);
        var today = TodayInLima();

        var response = await setup.Owner.GetAsync(Path(setup.Company.Id, today.AddDays(-1), today));
        Assert.StartsWith($"comprobantes-{setup.Ruc}-", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'), StringComparison.Ordinal);
        Assert.Equal("2", response.Headers.GetValues("X-SecureFact-Documents").Single());
        var (entries, index) = await ReadAsync(response);

        var one = $"{setup.Ruc}-01-F001-{accepted.Number}";
        var two = $"{setup.Ruc}-01-F001-{pending.Number}";
        Assert.Equal([$"cdr/R-{one}.zip", "indice.csv", $"xml/{one}.xml", $"xml/{two}.xml"], entries); // the CDR is only of the one that SUNAT answered
        var lines = index.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("archivo,tipo,serie,numero,fecha_emision,estado", lines[0], StringComparison.Ordinal);
        Assert.Equal(3, lines.Length);
        Assert.Contains($"{one},01,F001,{accepted.Number},{today:yyyy-MM-dd},Accepted,0,", lines[1], StringComparison.Ordinal);
        Assert.Contains(",6,20100066603,\"Cliente \"\"Comillas\"\", SAC\",PEN,118.00,", lines[1], StringComparison.Ordinal);
        Assert.EndsWith(",si", lines[1], StringComparison.Ordinal);
        Assert.Contains($"{two},01,F001,{pending.Number},{today:yyyy-MM-dd},ReadyToSend,,", lines[2], StringComparison.Ordinal);
        Assert.Contains(",'=SUMA(1+1) SAC,", lines[2], StringComparison.Ordinal); // what a buyer typed is never a formula in the spreadsheet
        Assert.EndsWith(",no", lines[2], StringComparison.Ordinal);

        // The XML is the one that was signed and sent.
        var xml = await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{acceptedId}/xml");
        using var zip = new ZipArchive(new MemoryStream(await (await setup.Owner.GetAsync(Path(setup.Company.Id, today, today))).Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry($"xml/{one}.xml")!.Open(), Encoding.UTF8);
        Assert.Equal(xml, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task The_type_and_the_dates_narrow_the_period_and_an_empty_one_is_a_zip_with_only_the_index()
    {
        var setup = await NewTenantAsync("Export filtros");
        var document = await IssueAsync(setup);
        await PrepareAsync(setup, document.Id);
        var today = TodayInLima();

        var (receipts, receiptIndex) = await ReadAsync(await setup.Owner.GetAsync(Path(setup.Company.Id, today.AddDays(-1), today, "03")));
        Assert.Equal(["indice.csv"], receipts);
        Assert.Single(receiptIndex.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries));

        var (earlier, _) = await ReadAsync(await setup.Owner.GetAsync(Path(setup.Company.Id, today.AddDays(-30), today.AddDays(-10))));
        Assert.Equal(["indice.csv"], earlier);

        var (invoices, _) = await ReadAsync(await setup.Owner.GetAsync(Path(setup.Company.Id, today, today, "01")));
        Assert.Equal(2, invoices.Count);
    }

    [Fact]
    public async Task A_period_that_is_backwards_too_long_or_of_a_wrong_type_is_refused()
    {
        var setup = await NewTenantAsync("Export inválido");
        var today = TodayInLima();

        foreach (var path in new[]
        {
            Path(setup.Company.Id, today, today.AddDays(-1)),
            Path(setup.Company.Id, today.AddDays(-366), today),
            Path(setup.Company.Id, today.AddDays(-5), today, "001"),
        })
        {
            var refused = await setup.Owner.GetAsync(path);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("SF-CPE-012", await CodeOf(refused));
        }

        Assert.Equal(HttpStatusCode.OK, (await setup.Owner.GetAsync(Path(setup.Company.Id, today.AddDays(-365), today))).StatusCode); // 366 days is the most
    }

    [Fact]
    public async Task A_company_of_another_account_cannot_be_exported_and_a_user_without_permission_to_read_documents_cannot_ask()
    {
        var mine = await NewTenantAsync("Export mía");
        var other = await NewTenantAsync("Export ajena");
        var today = TodayInLima();
        using var admin = await api.AdminClientAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await other.Owner.GetAsync(Path(mine.Company.Id, today, today))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(Path(mine.Company.Id, today, today))).StatusCode); // the platform does not read the documents of an account

        var developer = await ApiFixture.CreateUserAsync(admin, Roles.Developer, mine.TenantId);
        using var withoutPermission = api.ClientFor(await api.LoginOkAsync(developer.Email, developer.Password));
        Assert.Equal(HttpStatusCode.Forbidden, (await withoutPermission.GetAsync(Path(mine.Company.Id, today, today))).StatusCode);
        var reader = await ApiFixture.CreateUserAsync(admin, Roles.ReadOnly, mine.TenantId);
        using var readOnly = api.ClientFor(await api.LoginOkAsync(reader.Email, reader.Password));
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync(Path(mine.Company.Id, today, today))).StatusCode);
        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path(mine.Company.Id, today, today))).StatusCode);
    }

    [Fact]
    public async Task Each_export_is_audited_with_its_period_and_how_many_documents_it_took_and_nothing_of_their_content()
    {
        var setup = await NewTenantAsync("Export auditada");
        var document = await IssueAsync(setup);
        await PrepareAsync(setup, document.Id);
        var today = TodayInLima();

        Assert.Equal(HttpStatusCode.OK, (await setup.Owner.GetAsync(Path(setup.Company.Id, today, today))).StatusCode);

        var events = (await setup.Owner.GetFromJsonAsync<List<JsonElement>>($"/api/v1/audit?action={AuditActions.DocumentsExported}&take=10", ApiFixture.JsonOptions))!;
        var exported = Assert.Single(events);
        var values = exported.GetProperty("newValues").GetString()!;
        Assert.Contains("\"documents\":1", values, StringComparison.Ordinal);
        Assert.Contains($"\"from\":\"{Iso(today)}\"", values, StringComparison.Ordinal);
        Assert.DoesNotContain("Cliente SAC", values, StringComparison.Ordinal);
    }
}
