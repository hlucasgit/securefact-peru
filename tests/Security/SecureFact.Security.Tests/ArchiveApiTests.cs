using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SecureFact.Billing.Contracts;
using SecureFact.CpeEngine;
using SecureFact.CpeEngine.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;
using SecureFact.Platform.Messaging;
using SecureFact.Platform.Storage;
using SecureFact.SharedKernel.Storage;

namespace SecureFact.Security.Tests;

/// <summary>
/// The signed XML and the CDR of every electronic document are archived in object storage with their hash (ADR-005, ADR-036): queued in the transaction that stores them, written once,
/// listed with short-lived links, private to the tenant, and recovered by the reconciliation when an event was lost.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ArchiveApiTests(ApiFixture api)
{
    private static int _rucCounter = 30_000_000;

    private sealed record Setup(Guid TenantId, HttpClient Owner, CompanyDto Company, SeriesDto Invoice, string Ruc);

    private OutboxProcessor Processor() => new(api.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OutboxProcessor>.Instance);

    private InMemoryObjectStorage Storage => api.Services.GetRequiredService<InMemoryObjectStorage>();

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
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId = company.Id, solUser = "MODDATOS", solPassword = "Sol-Clave-arch-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/certificates", new { companyId = company.Id, pfxBase64 = PfxFor(ruc), password = "pw" })).StatusCode);
        return new Setup(tenantId, owner, company, series, ruc);
    }

    private static async Task<DocumentDto> IssueAsync(Setup setup)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
        {
            Content = JsonContent.Create(new
            {
                seriesId = setup.Invoice.Id,
                issueDate = Iso(TodayInLima()),
                currency = "PEN",
                buyer = new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
                lines = new[] { new { description = "Servicio de consultoría", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10" } } },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await setup.Owner.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDto>(ApiFixture.JsonOptions))!;
    }

    private static async Task<Guid> ElectronicIdAsync(Setup setup, Guid documentId) =>
        (await setup.Owner.GetFromJsonAsync<ElectronicDocumentDto>($"/api/v1/documents/{documentId}/electronic", ApiFixture.JsonOptions))!.Id;

    /// <summary>Runs the dispatcher until it has nothing left, as the worker does: an event can queue the next one.</summary>
    private async Task DrainAsync(Guid tenantId)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            await Processor().RunOnceAsync(CancellationToken.None, tenantId);
        }
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

    private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static string KeyOf(Setup setup, DocumentDto document, string kind) =>
        $"t/{setup.TenantId:N}/c/{setup.Company.Id:N}/{document.IssueDate.Year:D4}/01/{setup.Ruc}-01-{document.Series}-{document.Number}/{kind}/v1";

    private async Task AcceptAsync(Setup setup, DocumentDto document, Guid electronicId)
    {
        api.Sunat.Enqueue(ChannelReply.Cdr(FakeSunatChannel.CdrZip(setup.Ruc, $"{document.Series}-{document.Number}")));
        var sent = (await (await setup.Owner.PostAsync($"/api/v1/electronic-documents/{electronicId}/send", null)).Content.ReadFromJsonAsync<ElectronicDocumentDto>(ApiFixture.JsonOptions))!;
        Assert.Equal(EDocumentState.Accepted, sent.State);
    }

    [Fact]
    public async Task The_signed_xml_and_the_cdr_are_archived_with_their_hash_and_listed_with_download_links()
    {
        var setup = await NewTenantAsync("Archive SAC");
        var document = await IssueAsync(setup);
        await DrainAsync(setup.TenantId);
        var electronicId = await ElectronicIdAsync(setup, document.Id);

        // The signed XML is archived as soon as the document is prepared.
        var xml = Encoding.UTF8.GetBytes(await setup.Owner.GetStringAsync($"/api/v1/electronic-documents/{electronicId}/xml"));
        var xmlKey = KeyOf(setup, document, ArchiveKinds.SignedXml);
        var stored = await Storage.HeadAsync(xmlKey, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(Sha(xml), stored.Sha256);
        Assert.Equal(xml, await Storage.GetAsync(xmlKey, stored.Sha256, CancellationToken.None));
        Assert.StartsWith($"t/{setup.TenantId:N}/c/{setup.Company.Id:N}/", xmlKey, StringComparison.Ordinal); // a prefix is a tenant

        // The CDR follows the answer of SUNAT.
        await AcceptAsync(setup, document, electronicId);
        await DrainAsync(setup.TenantId);
        var cdr = await setup.Owner.GetByteArrayAsync($"/api/v1/electronic-documents/{electronicId}/cdr");
        var cdrKey = KeyOf(setup, document, ArchiveKinds.CdrZip);
        Assert.Equal(cdr, await Storage.GetAsync(cdrKey, Sha(cdr), CancellationToken.None));

        var listed = (await setup.Owner.GetFromJsonAsync<List<ArchivedFileDto>>($"/api/v1/electronic-documents/{electronicId}/archive", ApiFixture.JsonOptions))!;
        Assert.Equal([ArchiveKinds.CdrZip, ArchiveKinds.SignedXml], listed.Select(f => f.Kind));
        var listedXml = listed.Single(f => f.Kind == ArchiveKinds.SignedXml);
        Assert.Equal((Sha(xml), (long)xml.Length, "application/xml"), (listedXml.Sha256, listedXml.SizeBytes, listedXml.ContentType));
        Assert.Equal("application/zip", listed.Single(f => f.Kind == ArchiveKinds.CdrZip).ContentType);
        Assert.All(listed, f => Assert.True(f.DownloadExpiresAt > DateTimeOffset.UtcNow && f.DownloadExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(6)));
        Assert.All(listed, f => Assert.False(string.IsNullOrEmpty(f.DownloadUrl.ToString())));

        // The register is exact: one row per kind, with the key and the hash the file was stored with.
        Assert.Equal(2L, await ScalarAsync<long>($"SELECT count(*) FROM cpe.archived_file WHERE electronic_document_id = '{electronicId}'"));
        Assert.Equal(xmlKey, await ScalarAsync<string>($"SELECT storage_key FROM cpe.archived_file WHERE electronic_document_id = '{electronicId}' AND kind = 'signed-xml'"));
    }

    [Fact]
    public async Task Archiving_again_changes_nothing_and_a_different_content_for_a_stored_key_is_refused_without_replacing_it()
    {
        var setup = await NewTenantAsync("Archive Twice SAC");
        var first = await IssueAsync(setup);
        await DrainAsync(setup.TenantId);
        var electronicId = await ElectronicIdAsync(setup, first.Id);

        // The archiver run again for the same document registers nothing new.
        await using (var scope = api.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<SecureFact.Platform.Tenancy.DataScope>().UseTenant(new SecureFact.SharedKernel.Domain.TenantId(setup.TenantId));
            var archiver = scope.ServiceProvider.GetRequiredService<IDocumentArchive>();
            Assert.Single((await archiver.ListAsync(electronicId, CancellationToken.None)).Value, f => f.Kind == ArchiveKinds.SignedXml);
        }

        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM cpe.archived_file WHERE electronic_document_id = '{electronicId}'"));

        // Something else already holds the key of the next document: the archive does not overwrite it and says so.
        var second = await IssueAsync(setup);
        var occupied = KeyOf(setup, second, ArchiveKinds.SignedXml);
        var intruder = Encoding.UTF8.GetBytes("<not-the-signed-xml/>");
        await Storage.PutAsync(occupied, intruder, "application/xml", CancellationToken.None);

        await DrainAsync(setup.TenantId);

        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM cpe.archived_file WHERE tenant_id = '{setup.TenantId}'")); // only the first document
        Assert.Contains("already exists", await ScalarAsync<string>($"SELECT last_error FROM cpe.outbox_message WHERE tenant_id = '{setup.TenantId}' AND processed_at IS NULL AND last_error IS NOT NULL LIMIT 1"), StringComparison.Ordinal);
        Assert.Equal(intruder, await Storage.GetAsync(occupied, Sha(intruder), CancellationToken.None)); // untouched
    }

    [Fact]
    public async Task The_archive_of_a_document_is_private_to_its_tenant()
    {
        var mine = await NewTenantAsync("Archive Mine SAC");
        var theirs = await NewTenantAsync("Archive Theirs SAC");
        var document = await IssueAsync(mine);
        await DrainAsync(mine.TenantId);
        var electronicId = await ElectronicIdAsync(mine, document.Id);

        Assert.Equal(HttpStatusCode.OK, (await mine.Owner.GetAsync($"/api/v1/electronic-documents/{electronicId}/archive")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await theirs.Owner.GetAsync($"/api/v1/electronic-documents/{electronicId}/archive")).StatusCode);
        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/electronic-documents/{electronicId}/archive")).StatusCode);
    }

    [Fact]
    public async Task The_register_of_archived_files_is_insert_only_even_for_the_database_owner()
    {
        var setup = await NewTenantAsync("Archive Immutable SAC");
        var document = await IssueAsync(setup);
        await DrainAsync(setup.TenantId);
        var electronicId = await ElectronicIdAsync(setup, document.Id);

        foreach (var sql in new[]
        {
            $"UPDATE cpe.archived_file SET sha256 = repeat('0', 64) WHERE electronic_document_id = '{electronicId}'",
            $"UPDATE cpe.archived_file SET storage_key = 'elsewhere' WHERE electronic_document_id = '{electronicId}'",
            $"DELETE FROM cpe.archived_file WHERE electronic_document_id = '{electronicId}'",
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM cpe.archived_file WHERE electronic_document_id = '{electronicId}'"));
    }

    [Fact]
    public async Task A_document_whose_event_was_lost_is_found_by_the_reconciliation_and_archived()
    {
        var setup = await NewTenantAsync("Archive Reconcile SAC");
        var document = await IssueAsync(setup);

        // Billing's event runs and prepares the document; the CPE engine's event is then lost (as it would be for a document that predates the archive).
        await Processor().RunOnceAsync(CancellationToken.None, setup.TenantId);
        var electronicId = await ElectronicIdAsync(setup, document.Id);
        await ExecuteAsync("ALTER TABLE cpe.outbox_message DISABLE TRIGGER outbox_message_guard");
        try
        {
            await ExecuteAsync($"DELETE FROM cpe.outbox_message WHERE tenant_id = '{setup.TenantId}'");
        }
        finally
        {
            await ExecuteAsync("ALTER TABLE cpe.outbox_message ENABLE TRIGGER outbox_message_guard");
        }

        await DrainAsync(setup.TenantId);
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM cpe.archived_file WHERE electronic_document_id = '{electronicId}'"));

        await api.Services.GetRequiredService<ICpeWorkProcessor>().RunOnceAsync(CancellationToken.None, setup.TenantId);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM cpe.outbox_message WHERE tenant_id = '{setup.TenantId}' AND event_type = '{CpeEvents.DocumentPrepared}'"));

        // A second pass does not queue it again while the event waits, and delivering it archives the file.
        await api.Services.GetRequiredService<ICpeWorkProcessor>().RunOnceAsync(CancellationToken.None, setup.TenantId);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM cpe.outbox_message WHERE tenant_id = '{setup.TenantId}' AND event_type = '{CpeEvents.DocumentPrepared}'"));
        await DrainAsync(setup.TenantId);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM cpe.archived_file WHERE electronic_document_id = '{electronicId}' AND kind = 'signed-xml'"));

        // Once archived, the reconciliation leaves the document alone.
        await api.Services.GetRequiredService<ICpeWorkProcessor>().RunOnceAsync(CancellationToken.None, setup.TenantId);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM cpe.outbox_message WHERE tenant_id = '{setup.TenantId}' AND event_type = '{CpeEvents.DocumentPrepared}'"));
    }

    [Fact]
    public async Task The_events_of_the_cpe_engine_are_immutable_and_follow_the_same_retention_as_the_others()
    {
        var setup = await NewTenantAsync("Archive Outbox SAC");
        await IssueAsync(setup);
        await DrainAsync(setup.TenantId);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM cpe.outbox_message WHERE tenant_id = '{setup.TenantId}' AND processed_at IS NOT NULL"));

        foreach (var sql in new[]
        {
            $"UPDATE cpe.outbox_message SET payload = '[]' WHERE tenant_id = '{setup.TenantId}'",
            $"DELETE FROM cpe.outbox_message WHERE tenant_id = '{setup.TenantId}'",
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        await ExecuteAsync($"UPDATE cpe.outbox_message SET processed_at = now() - interval '40 days' WHERE tenant_id = '{setup.TenantId}'");
        Assert.True(await Processor().PurgeAsync(TimeSpan.FromDays(30), CancellationToken.None) >= 1);
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM cpe.outbox_message WHERE tenant_id = '{setup.TenantId}'"));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT count(*) FROM cpe.archived_file WHERE tenant_id = '{setup.TenantId}'")); // the archive outlives its event
    }
}
