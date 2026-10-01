using System.Net;
using System.Net.Http.Json;
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
public sealed class SolCredentialsApiTests(ApiFixture api)
{
    private const string SolPassword = "Sol-Clave-that-must-not-leak-9";
    private static int _rucCounter = 7_000_000;

    private sealed record Setup(Guid TenantId, HttpClient Owner, Guid CompanyId);

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
        var response = await owner.PostAsJsonAsync("/api/v1/companies", new { ruc = NewRuc(), details = new { legalName = "Emisora SAC", fiscalAddress = "Av. Larco 123", ubigeo = "150122" } });
        var company = (await response.Content.ReadFromJsonAsync<CompanyDto>(ApiFixture.JsonOptions))!;
        return new Setup(tenantId, owner, company.Id);
    }

    private static Task<HttpResponseMessage> SetAsync(HttpClient client, Guid companyId, string user = "MODDATOS", string password = SolPassword) =>
        client.PutAsJsonAsync("/api/v1/sol-credentials", new { companyId, solUser = user, solPassword = password });

    [Fact]
    public async Task Credentials_are_stored_encrypted_never_echoed_and_recoverable_by_the_pipeline()
    {
        var setup = await NewTenantAsync("Sol Basic SAC");

        var response = await SetAsync(setup.Owner, setup.CompanyId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SolPassword, raw, StringComparison.Ordinal);
        var dto = JsonSerializer.Deserialize<SolCredentialDto>(raw, ApiFixture.JsonOptions)!;
        Assert.Equal("MODDATOS", dto.SolUser);
        Assert.True(dto.HasPassword);
        Assert.DoesNotContain(SolPassword, await setup.Owner.GetStringAsync($"/api/v1/sol-credentials/{setup.CompanyId}"), StringComparison.Ordinal);

        await using var connection = new NpgsqlConnection(api.Postgres.OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT protected_password FROM certificates.sol_credential WHERE company_id = @c", connection);
        command.Parameters.AddWithValue("c", setup.CompanyId);
        var stored = (byte[])(await command.ExecuteScalarAsync())!;
        Assert.DoesNotContain(SolPassword, Encoding.Latin1.GetString(stored), StringComparison.Ordinal);
        Assert.DoesNotContain(SolPassword, string.Join(Environment.NewLine, api.Logs.Snapshot()), StringComparison.Ordinal);

        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UseTenant(new TenantId(setup.TenantId));
        var secret = await scope.ServiceProvider.GetRequiredService<ISolCredentialProvider>().GetAsync(setup.CompanyId, CancellationToken.None);
        Assert.True(secret.IsSuccess);
        Assert.Equal(("MODDATOS", SolPassword), (secret.Value.SolUser, secret.Value.SolPassword));
        Assert.DoesNotContain(SolPassword, secret.Value.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replacing_and_clearing_credentials_work_and_are_audited_without_the_password()
    {
        var setup = await NewTenantAsync("Sol Replace SAC");
        await SetAsync(setup.Owner, setup.CompanyId);

        var replaced = await SetAsync(setup.Owner, setup.CompanyId, "OTROUSER", "Another-Sol-Clave-1");
        Assert.Equal("OTROUSER", (await replaced.Content.ReadFromJsonAsync<SolCredentialDto>(ApiFixture.JsonOptions))!.SolUser);

        Assert.Equal(HttpStatusCode.NoContent, (await setup.Owner.DeleteAsync($"/api/v1/sol-credentials/{setup.CompanyId}")).StatusCode);
        Assert.False((await setup.Owner.GetFromJsonAsync<SolCredentialDto>($"/api/v1/sol-credentials/{setup.CompanyId}", ApiFixture.JsonOptions))!.HasPassword);

        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UseTenant(new TenantId(setup.TenantId));
        var secret = await scope.ServiceProvider.GetRequiredService<ISolCredentialProvider>().GetAsync(setup.CompanyId, CancellationToken.None);
        Assert.Equal("SF-CRT-004", secret.Error.Code);

        var events = (await setup.Owner.GetFromJsonAsync<List<AuditRecord>>("/api/v1/audit?take=200", ApiFixture.JsonOptions))!
            .Where(e => e.Action is AuditActions.SolCredentialsSet or AuditActions.SolCredentialsCleared).ToList();
        Assert.Equal(3, events.Count);
        Assert.All(events, e =>
        {
            Assert.DoesNotContain(SolPassword, e.NewValues + e.OldValues, StringComparison.Ordinal);
            Assert.DoesNotContain("Another-Sol-Clave-1", e.NewValues + e.OldValues, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("", "pass")]
    [InlineData("MOD DATOS", "pass")]
    [InlineData("MODDATOS", "")]
    public async Task Invalid_credentials_are_refused(string user, string password)
    {
        var setup = await NewTenantAsync($"Sol Invalid {user.Length}{password.Length} SAC");

        var response = await SetAsync(setup.Owner, setup.CompanyId, user, password);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await setup.Owner.GetAsync($"/api/v1/sol-credentials/{setup.CompanyId}")).StatusCode);
    }

    [Fact]
    public async Task Credentials_are_isolated_between_tenants_and_guarded_by_role()
    {
        var a = await NewTenantAsync("Sol Iso A SAC");
        var b = await NewTenantAsync("Sol Iso B SAC");
        await SetAsync(a.Owner, a.CompanyId);

        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync($"/api/v1/sol-credentials/{a.CompanyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.DeleteAsync($"/api/v1/sol-credentials/{a.CompanyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetAsync(b.Owner, a.CompanyId, "HACKER", "x-y-z-1234")).StatusCode);

        var sales = await ApiFixture.CreateUserAsync(a.Owner, Roles.Sales, a.TenantId);
        var auditor = await ApiFixture.CreateUserAsync(a.Owner, Roles.Auditor, a.TenantId);
        using var salesClient = api.ClientFor(await api.LoginOkAsync(sales.Email, sales.Password));
        using var auditorClient = api.ClientFor(await api.LoginOkAsync(auditor.Email, auditor.Password));
        Assert.Equal(HttpStatusCode.Forbidden, (await salesClient.GetAsync($"/api/v1/sol-credentials/{a.CompanyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetAsync(salesClient, a.CompanyId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await auditorClient.GetAsync($"/api/v1/sol-credentials/{a.CompanyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetAsync(auditorClient, a.CompanyId)).StatusCode);
    }
}
