using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SecureFact.Audit.Contracts;
using SecureFact.Certificates.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Organizations.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Domain;

namespace SecureFact.Security.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class CertificatesApiTests(ApiFixture api)
{
    private const string PfxPassword = "pfx-Passw0rd-that-must-not-leak";
    private static int _rucCounter = 5_000_000;

    private sealed record Setup(Guid TenantId, HttpClient Owner, Guid CompanyId, string Ruc);

    private static string NewRuc()
    {
        var body = "20" + Interlocked.Increment(ref _rucCounter).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var sum = body.Select((c, i) => (c - '0') * weights[i]).Sum();
        return body + ((11 - (sum % 11)) % 10);
    }

    private async Task<Setup> NewTenantAsync(string name)
    {
        var tenantId = await api.CreateTenantAsync(name);
        using var admin = await api.AdminClientAsync();
        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        var owner = api.ClientFor(await api.LoginOkAsync(user.Email, user.Password));
        var ruc = NewRuc();
        var response = await owner.PostAsJsonAsync("/api/v1/companies", new { ruc, details = new { legalName = "Emisora SAC", fiscalAddress = "Av. Larco 123", ubigeo = "150122" } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var company = (await response.Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!;
        return new Setup(tenantId, owner, company.Id, ruc);
    }

    private static string Pfx(string subjectRuc, int keySize = 2048, int validFromDays = -1, int validToDays = 300, string password = PfxPassword, bool withKey = true)
    {
        using var rsa = RSA.Create(keySize);
        var request = new CertificateRequest($"CN=Representante Demo, OU={subjectRuc}, O=EMISORA DEMO SAC, C=PE", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(validFromDays), DateTimeOffset.UtcNow.AddDays(validToDays));
        return Convert.ToBase64String(withKey ? certificate.Export(X509ContentType.Pfx, password) : certificate.Export(X509ContentType.Cert));
    }

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid companyId, string pfxBase64, string password = PfxPassword) =>
        client.PostAsJsonAsync("/api/v1/certificates", new { companyId, pfxBase64, password });

    [Fact]
    public async Task A_certificate_is_stored_encrypted_and_never_echoed()
    {
        var setup = await NewTenantAsync("Certs Basic SAC");
        var pfx = Pfx(setup.Ruc);

        var response = await UploadAsync(setup.Owner, setup.CompanyId, pfx);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(PfxPassword, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(pfx[..40], raw, StringComparison.Ordinal);
        var dto = JsonSerializer.Deserialize<CertificateDto>(raw, ApiFixture.JsonOptions)!;
        Assert.True(dto.IsActive);
        Assert.True(dto.RucInSubject);
        Assert.Contains(setup.Ruc, dto.Subject, StringComparison.Ordinal);

        // At rest: the stored blob is ciphertext, not a PKCS#12 file and not readable text.
        await using var connection = new NpgsqlConnection(api.Postgres.OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT protected_pfx FROM certificates.company_certificate WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", dto.Id);
        var stored = (byte[])(await command.ExecuteScalarAsync())!;
        Assert.DoesNotContain("Representante", Encoding.Latin1.GetString(stored), StringComparison.Ordinal);
        Assert.Throws<CryptographicException>(() => X509CertificateLoader.LoadPkcs12(stored, null));
        Assert.Throws<CryptographicException>(() => X509CertificateLoader.LoadPkcs12(stored, PfxPassword));

        var logs = string.Join(Environment.NewLine, api.Logs.Snapshot());
        Assert.DoesNotContain(PfxPassword, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(pfx[..60], logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_stored_certificate_can_be_recovered_by_the_signing_pipeline_and_signs()
    {
        var setup = await NewTenantAsync("Certs Provider SAC");
        var dto = (await (await UploadAsync(setup.Owner, setup.CompanyId, Pfx(setup.Ruc))).Content.ReadFromJsonAsync<CertificateDto>(ApiFixture.JsonOptions))!;

        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UseTenant(new TenantId(setup.TenantId));
        var provider = scope.ServiceProvider.GetRequiredService<ICertificateProvider>();
        var result = await provider.GetActiveSigningCertificateAsync(setup.CompanyId, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Detail);
        using var certificate = result.Value;
        Assert.Equal(dto.Thumbprint, certificate.Thumbprint);
        using var rsa = certificate.GetRSAPrivateKey()!;
        byte[] data = [1, 2, 3];
        var signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Assert.True(certificate.GetRSAPublicKey()!.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task A_new_certificate_replaces_the_active_one_and_the_old_one_is_kept()
    {
        var setup = await NewTenantAsync("Certs Replace SAC");
        var first = (await (await UploadAsync(setup.Owner, setup.CompanyId, Pfx(setup.Ruc))).Content.ReadFromJsonAsync<CertificateDto>(ApiFixture.JsonOptions))!;
        var second = (await (await UploadAsync(setup.Owner, setup.CompanyId, Pfx(setup.Ruc))).Content.ReadFromJsonAsync<CertificateDto>(ApiFixture.JsonOptions))!;

        var all = (await setup.Owner.GetFromJsonAsync<List<CertificateDto>>($"/api/v1/certificates?companyId={setup.CompanyId}", ApiFixture.JsonOptions))!;

        Assert.Equal(2, all.Count);
        Assert.Single(all, c => c.IsActive);
        Assert.True(all.Single(c => c.Id == second.Id).IsActive);
        var old = all.Single(c => c.Id == first.Id);
        Assert.False(old.IsActive);
        Assert.NotNull(old.DeactivatedAt);
    }

    [Fact]
    public async Task Uploading_the_same_certificate_twice_is_a_conflict()
    {
        var setup = await NewTenantAsync("Certs Duplicate SAC");
        var pfx = Pfx(setup.Ruc);
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(setup.Owner, setup.CompanyId, pfx)).StatusCode);

        var again = await UploadAsync(setup.Owner, setup.CompanyId, pfx);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("SF-CRT-003", await ProblemCodeAsync(again));
    }

    [Fact]
    public async Task Unsuitable_certificates_are_refused_and_nothing_is_stored()
    {
        var setup = await NewTenantAsync("Certs Invalid SAC");
        var otherRuc = NewRuc();
        var cases = new Dictionary<string, HttpResponseMessage>
        {
            ["wrong password"] = await UploadAsync(setup.Owner, setup.CompanyId, Pfx(setup.Ruc), "not-the-password"),
            ["garbage"] = await UploadAsync(setup.Owner, setup.CompanyId, Convert.ToBase64String("not a pfx file"u8.ToArray())),
            ["not base64"] = await UploadAsync(setup.Owner, setup.CompanyId, "***"),
            ["empty"] = await UploadAsync(setup.Owner, setup.CompanyId, string.Empty),
            ["no private key"] = await UploadAsync(setup.Owner, setup.CompanyId, Pfx(setup.Ruc, withKey: false)),
            ["1024-bit key"] = await UploadAsync(setup.Owner, setup.CompanyId, Pfx(setup.Ruc, keySize: 1024)),
            ["expired"] = await UploadAsync(setup.Owner, setup.CompanyId, Pfx(setup.Ruc, validFromDays: -400, validToDays: -30)),
            ["not yet valid"] = await UploadAsync(setup.Owner, setup.CompanyId, Pfx(setup.Ruc, validFromDays: 10, validToDays: 300)),
            ["another taxpayer"] = await UploadAsync(setup.Owner, setup.CompanyId, Pfx(otherRuc)),
            ["oversized"] = await UploadAsync(setup.Owner, setup.CompanyId, Convert.ToBase64String(new byte[200 * 1024])),
        };

        foreach (var (name, response) in cases)
        {
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{name}: {response.StatusCode}");
            Assert.Equal("SF-CRT-001", await ProblemCodeAsync(response));
        }

        // A wrong password and a corrupt file are indistinguishable to the caller.
        var wrong = await cases["wrong password"].Content.ReadAsStringAsync();
        var corrupt = await cases["garbage"].Content.ReadAsStringAsync();
        Assert.Equal(Detail(wrong), Detail(corrupt));
        Assert.Empty((await setup.Owner.GetFromJsonAsync<List<CertificateDto>>($"/api/v1/certificates?companyId={setup.CompanyId}", ApiFixture.JsonOptions))!);

        static string Detail(string json) => JsonDocument.Parse(json).RootElement.GetProperty("detail").GetString()!;
    }

    [Fact]
    public async Task A_subject_without_any_ruc_is_accepted_but_flagged()
    {
        var setup = await NewTenantAsync("Certs NoRuc SAC");

        var response = await UploadAsync(setup.Owner, setup.CompanyId, Pfx("DEPARTAMENTO DE FACTURACION"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<CertificateDto>(ApiFixture.JsonOptions))!.RucInSubject);
    }

    [Fact]
    public async Task Certificates_are_isolated_between_tenants()
    {
        var a = await NewTenantAsync("Certs Iso A SAC");
        var b = await NewTenantAsync("Certs Iso B SAC");
        var dto = (await (await UploadAsync(a.Owner, a.CompanyId, Pfx(a.Ruc))).Content.ReadFromJsonAsync<CertificateDto>(ApiFixture.JsonOptions))!;

        Assert.Empty((await b.Owner.GetFromJsonAsync<List<CertificateDto>>($"/api/v1/certificates?companyId={a.CompanyId}", ApiFixture.JsonOptions))!);
        Assert.DoesNotContain((await b.Owner.GetFromJsonAsync<List<CertificateDto>>("/api/v1/certificates/expiring?days=3650", ApiFixture.JsonOptions))!, c => c.Id == dto.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PostAsync($"/api/v1/certificates/{dto.Id}/deactivate", null)).StatusCode);

        var hijack = await UploadAsync(b.Owner, a.CompanyId, Pfx(a.Ruc));
        Assert.Equal(HttpStatusCode.NotFound, hijack.StatusCode);
        Assert.Equal("SF-ORG-002", await ProblemCodeAsync(hijack));

        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UseTenant(new TenantId(b.TenantId));
        var stolen = await scope.ServiceProvider.GetRequiredService<ICertificateProvider>().GetActiveSigningCertificateAsync(a.CompanyId, CancellationToken.None);
        Assert.False(stolen.IsSuccess);
        Assert.Equal("SF-CRT-004", stolen.Error.Code);
    }

    [Fact]
    public async Task Roles_control_certificate_access()
    {
        var setup = await NewTenantAsync("Certs Rbac SAC");
        await UploadAsync(setup.Owner, setup.CompanyId, Pfx(setup.Ruc));

        async Task<HttpClient> ClientAsync(string role)
        {
            var user = await ApiFixture.CreateUserAsync(setup.Owner, role, setup.TenantId);
            return api.ClientFor(await api.LoginOkAsync(user.Email, user.Password));
        }

        using var auditor = await ClientAsync(Roles.Auditor);
        using var sales = await ClientAsync(Roles.Sales);
        using var readOnly = await ClientAsync(Roles.ReadOnly);
        using var anonymous = api.NewClient();

        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync($"/api/v1/certificates?companyId={setup.CompanyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsJsonAsync("/api/v1/certificates", new { companyId = setup.CompanyId, pfxBase64 = Pfx(setup.Ruc), password = PfxPassword })).StatusCode);
        foreach (var client in new[] { sales, readOnly })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/certificates?companyId={setup.CompanyId}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/certificates", new { companyId = setup.CompanyId, pfxBase64 = Pfx(setup.Ruc), password = PfxPassword })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/certificates?companyId={setup.CompanyId}")).StatusCode);
    }

    [Fact]
    public async Task Expiring_certificates_are_reported_and_a_deactivated_one_cannot_sign()
    {
        var setup = await NewTenantAsync("Certs Expiry SAC");
        var dto = (await (await UploadAsync(setup.Owner, setup.CompanyId, Pfx(setup.Ruc, validToDays: 10))).Content.ReadFromJsonAsync<CertificateDto>(ApiFixture.JsonOptions))!;

        Assert.Contains((await setup.Owner.GetFromJsonAsync<List<CertificateDto>>("/api/v1/certificates/expiring?days=30", ApiFixture.JsonOptions))!, c => c.Id == dto.Id);
        Assert.DoesNotContain((await setup.Owner.GetFromJsonAsync<List<CertificateDto>>("/api/v1/certificates/expiring?days=5", ApiFixture.JsonOptions))!, c => c.Id == dto.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await setup.Owner.PostAsync($"/api/v1/certificates/{dto.Id}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Owner.PostAsync($"/api/v1/certificates/{dto.Id}/deactivate", null)).StatusCode); // idempotent
        Assert.DoesNotContain((await setup.Owner.GetFromJsonAsync<List<CertificateDto>>("/api/v1/certificates/expiring?days=30", ApiFixture.JsonOptions))!, c => c.Id == dto.Id);

        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UseTenant(new TenantId(setup.TenantId));
        var result = await scope.ServiceProvider.GetRequiredService<ICertificateProvider>().GetActiveSigningCertificateAsync(setup.CompanyId, CancellationToken.None);
        Assert.Equal("SF-CRT-004", result.Error.Code);
    }

    [Fact]
    public async Task Uploads_are_audited_without_secrets()
    {
        var setup = await NewTenantAsync("Certs Audit SAC");
        var pfx = Pfx(setup.Ruc);
        var dto = (await (await UploadAsync(setup.Owner, setup.CompanyId, pfx)).Content.ReadFromJsonAsync<CertificateDto>(ApiFixture.JsonOptions))!;

        var events = (await setup.Owner.GetFromJsonAsync<List<AuditRecord>>($"/api/v1/audit?action={AuditActions.CertificateUploaded}&take=50", ApiFixture.JsonOptions))!;

        var record = Assert.Single(events, e => e.EntityId == dto.Id.ToString("D"));
        Assert.Contains(dto.Thumbprint, record.NewValues, StringComparison.Ordinal);
        Assert.DoesNotContain(PfxPassword, record.NewValues, StringComparison.Ordinal);
        Assert.DoesNotContain(pfx[..60], record.NewValues, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_runtime_role_cannot_delete_certificates()
    {
        await using var connection = new NpgsqlConnection(api.Postgres.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("DELETE FROM certificates.company_certificate", connection);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal("42501", failure.SqlState);
    }
}
