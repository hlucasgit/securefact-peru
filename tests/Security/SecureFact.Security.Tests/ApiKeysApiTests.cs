using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using SecureFact.Identity.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>API keys for programs (ADR-066): created by a person of the tenant, they act with one role of the tenant and nothing else.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ApiKeysApiTests(ApiFixture api)
{
    private static int _rucCounter = 18_000_000;

    private sealed record KeyRow(Guid Id, string Name, string Role, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);

    private sealed record Created(KeyRow Key, string Secret);

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

    private async Task<(Guid TenantId, HttpClient Owner)> TenantAsync(string name)
    {
        using var admin = await api.AdminClientAsync();
        var tenantId = await api.CreateTenantAsync(name);
        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        return (tenantId, api.ClientFor(await api.LoginOkAsync(user.Email, user.Password)));
    }

    private static async Task<Created> NewKeyAsync(HttpClient owner, string role = Roles.Sales, string name = "Integración de ventas", DateTimeOffset? expiresAt = null)
    {
        var response = await owner.PostAsJsonAsync("/api/v1/api-keys", new { name, role, expiresAt });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Created>(ApiFixture.JsonOptions))!;
    }

    private HttpClient WithKey(string secret, bool header = false)
    {
        var client = api.NewClient();
        if (header)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", secret);
        }
        else
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }

        return client;
    }

    private static Task<HttpResponseMessage> NewCompanyAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/v1/companies", new { ruc = NewRuc(), details = new { legalName = "Emisora SAC", fiscalAddress = "Av. Larco 123", ubigeo = "150122" } });

    [Fact]
    public async Task A_key_is_shown_once_listed_without_its_secret_and_works_in_either_header()
    {
        var (_, owner) = await TenantAsync("Llaves SAC");
        var created = await NewKeyAsync(owner);

        Assert.StartsWith("sfk_", created.Secret, StringComparison.Ordinal);
        Assert.Contains(created.Key.Id.ToString("N"), created.Secret, StringComparison.Ordinal);
        Assert.Equal(("Integración de ventas", "Sales"), (created.Key.Name, created.Key.Role));
        var listed = (await owner.GetFromJsonAsync<List<KeyRow>>("/api/v1/api-keys", ApiFixture.JsonOptions))!;
        var row = Assert.Single(listed);
        Assert.Equal(created.Key.Prefix, row.Prefix);
        var raw = await owner.GetStringAsync("/api/v1/api-keys");
        Assert.DoesNotContain(created.Secret, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", raw, StringComparison.OrdinalIgnoreCase);

        using var bearer = WithKey(created.Secret);
        using var header = WithKey(created.Secret, header: true);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/v1/companies")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await header.GetAsync("/api/v1/companies")).StatusCode);
        var used = (await owner.GetFromJsonAsync<List<KeyRow>>("/api/v1/api-keys", ApiFixture.JsonOptions))!.Single();
        Assert.NotNull(used.LastUsedAt);
    }

    [Fact]
    public async Task A_key_has_the_permissions_of_its_role_and_never_those_that_manage_the_tenant()
    {
        var (_, owner) = await TenantAsync("Roles de llaves SAC");
        var sales = await NewKeyAsync(owner, Roles.Sales);
        var readOnly = await NewKeyAsync(owner, Roles.ReadOnly, "Solo lectura");
        using var salesClient = WithKey(sales.Secret);
        using var readClient = WithKey(readOnly.Secret);

        Assert.Equal(HttpStatusCode.Created, (await NewCompanyAsync(owner)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await salesClient.GetAsync("/api/v1/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readClient.GetAsync("/api/v1/companies")).StatusCode);
        // A reader cannot create a customer; neither key can create a company (the role does not manage companies).
        Assert.Equal(HttpStatusCode.Forbidden, (await NewCompanyAsync(readClient)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await NewCompanyAsync(salesClient)).StatusCode);
        // No key manages the keys, the users, the webhooks or the plan of the account.
        foreach (var client in new[] { salesClient, readClient })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/api-keys")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/api-keys", new { name = "Otra llave", role = "ReadOnly" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/users")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/platform/tenants")).StatusCode);
        }
    }

    [Fact]
    public async Task A_key_sees_only_the_data_of_its_tenant()
    {
        var (_, ownerA) = await TenantAsync("Llave A SAC");
        var (_, ownerB) = await TenantAsync("Llave B SAC");
        var companyA = (await (await NewCompanyAsync(ownerA)).Content.ReadFromJsonAsync<JsonElement>(ApiFixture.JsonOptions)).GetProperty("id").GetGuid();
        var companyB = (await (await NewCompanyAsync(ownerB)).Content.ReadFromJsonAsync<JsonElement>(ApiFixture.JsonOptions)).GetProperty("id").GetGuid();
        var keyA = await NewKeyAsync(ownerA, Roles.ReadOnly);
        using var client = WithKey(keyA.Secret);

        var companies = (await client.GetFromJsonAsync<List<JsonElement>>("/api/v1/companies?take=100", ApiFixture.JsonOptions))!;
        Assert.Equal(companyA, Assert.Single(companies).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/companies/{companyB}")).StatusCode);
        // Nobody's key lists the keys of another tenant.
        Assert.DoesNotContain(keyA.Key.Id, (await ownerB.GetFromJsonAsync<List<KeyRow>>("/api/v1/api-keys", ApiFixture.JsonOptions))!.Select(k => k.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.PostAsync($"/api/v1/api-keys/{keyA.Key.Id}/revoke", null)).StatusCode);
    }

    [Fact]
    public async Task A_wrong_revoked_expired_or_altered_key_gets_the_same_answer_as_no_key_and_a_revoked_one_stops_at_once()
    {
        var (_, owner) = await TenantAsync("Llaves inválidas SAC");
        var key = await NewKeyAsync(owner, Roles.ReadOnly);
        var short_ = await NewKeyAsync(owner, Roles.ReadOnly, "Vence pronto", DateTimeOffset.UtcNow.AddSeconds(2));
        using var good = WithKey(key.Secret);
        Assert.Equal(HttpStatusCode.OK, (await good.GetAsync("/api/v1/companies")).StatusCode);

        var tampered = key.Secret[..^2] + (key.Secret[^1] == 'a' ? "bb" : "aa");
        foreach (var presented in new[] { tampered, "sfk_" + Guid.NewGuid().ToString("N") + "_" + new string('x', 43), "sfk_basura", "sfk_", "sfk_" + key.Key.Id.ToString("N") + "_" })
        {
            using var client = WithKey(presented);
            var response = await client.GetAsync("/api/v1/companies");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        await Task.Delay(TimeSpan.FromSeconds(2.5));
        using var expired = WithKey(short_.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await expired.GetAsync("/api/v1/companies")).StatusCode);

        var revoked = await owner.PostAsync($"/api/v1/api-keys/{key.Key.Id}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.NotNull((await revoked.Content.ReadFromJsonAsync<KeyRow>(ApiFixture.JsonOptions))!.RevokedAt);
        Assert.Equal(HttpStatusCode.Unauthorized, (await good.GetAsync("/api/v1/companies")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/v1/api-keys/{key.Key.Id}/revoke", null)).StatusCode); // revoking again changes nothing
    }

    [Fact]
    public async Task Only_a_person_with_the_permission_creates_keys_and_never_a_role_above_their_own_or_an_administrator_role()
    {
        using var admin = await api.AdminClientAsync();
        var (tenantId, owner) = await TenantAsync("Quién crea llaves SAC");
        var seller = await ApiFixture.CreateUserAsync(owner, Roles.Sales, tenantId);
        using var sellerClient = api.ClientFor(await api.LoginOkAsync(seller.Email, seller.Password));
        var support = await ApiFixture.CreateUserAsync(admin, Roles.PlatformSupport, null);
        using var supportClient = api.ClientFor(await api.LoginOkAsync(support.Email, support.Password));

        Assert.Equal(HttpStatusCode.Forbidden, (await sellerClient.PostAsJsonAsync("/api/v1/api-keys", new { name = "Mi llave", role = "Sales" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sellerClient.GetAsync("/api/v1/api-keys")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/v1/api-keys", new { name = "De plataforma", role = "Sales" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await supportClient.GetAsync("/api/v1/api-keys")).StatusCode);
        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/api-keys")).StatusCode);

        foreach (var (name, role) in new[] { ("Dueño", "TenantOwner"), ("Admin", "TenantAdmin"), ("Plataforma", "PlatformSuperAdmin"), ("Inexistente", "Root"), ("Vacío", "") })
        {
            var refused = await owner.PostAsJsonAsync("/api/v1/api-keys", new { name = $"{name} de prueba", role });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("SF-KEY-001", await CodeAsync(refused));
        }

        foreach (var body in new object[]
        {
            new { name = "ab", role = "Sales" },
            new { name = new string('x', 61), role = "Sales" },
            new { name = "Vence ya", role = "Sales", expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
            new { name = "Vence lejos", role = "Sales", expiresAt = DateTimeOffset.UtcNow.AddYears(5) },
        })
        {
            Assert.Equal("SF-KEY-001", await CodeAsync(await owner.PostAsJsonAsync("/api/v1/api-keys", body)));
        }

        var roles = (await owner.GetFromJsonAsync<List<string>>("/api/v1/api-keys/roles", ApiFixture.JsonOptions))!;
        Assert.Equal(["BillingAdmin", "Sales", "Accountant", "Auditor", "ReadOnly"], roles);
    }

    [Fact]
    public async Task A_tenant_has_a_limited_number_of_active_keys_and_revoking_one_frees_its_place()
    {
        var (_, owner) = await TenantAsync("Muchas llaves SAC");
        var first = await NewKeyAsync(owner, name: "Llave 0");
        for (var i = 1; i < 20; i++)
        {
            await NewKeyAsync(owner, name: $"Llave {i}");
        }

        var refused = await owner.PostAsJsonAsync("/api/v1/api-keys", new { name = "Una más", role = "Sales" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("SF-KEY-003", await CodeAsync(refused));

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/v1/api-keys/{first.Key.Id}/revoke", null)).StatusCode);
        await NewKeyAsync(owner, name: "Una más");
    }

    [Fact]
    public async Task A_key_of_a_suspended_tenant_is_refused_and_works_again_when_the_tenant_does()
    {
        using var admin = await api.AdminClientAsync();
        var (tenantId, owner) = await TenantAsync("Suspendida con llave SAC");
        var key = await NewKeyAsync(owner, Roles.ReadOnly);
        using var client = WithKey(key.Secret);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/companies")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/status", new { status = "Suspended", reason = "Prueba de suspensión" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/companies")).StatusCode); // the status changed in this process: its cache was dropped

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/status", new { status = "Active", reason = "Resuelto" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/companies")).StatusCode);
    }

    [Fact]
    public async Task The_creation_and_the_revocation_of_a_key_are_audited_without_the_secret()
    {
        using var admin = await api.AdminClientAsync();
        var (tenantId, owner) = await TenantAsync("Auditoría de llaves SAC");
        var key = await NewKeyAsync(owner);
        await owner.PostAsync($"/api/v1/api-keys/{key.Key.Id}/revoke", null);

        var events = (await admin.GetFromJsonAsync<List<JsonElement>>($"/api/v1/audit?tenantId={tenantId}&entityType=api_key&take=20", ApiFixture.JsonOptions))!;

        Assert.Equal(["identity.api_key.revoked", "identity.api_key.created"], events.Select(e => e.GetProperty("action").GetString()));
        Assert.DoesNotContain(key.Secret, JsonSerializer.Serialize(events), StringComparison.Ordinal);
        Assert.Contains(api.Logs.Snapshot(), entry => entry.Contains("/api/v1/api-keys", StringComparison.Ordinal));
        Assert.DoesNotContain(api.Logs.Snapshot(), entry => entry.Contains(key.Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_secret_is_never_stored_only_its_hash()
    {
        var (_, owner) = await TenantAsync("Hash de llaves SAC");
        var key = await NewKeyAsync(owner);

        await using var connection = new NpgsqlConnection(api.Postgres.OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT secret_hash FROM identity.api_key WHERE id = '{key.Key.Id}'", connection);
        var hash = (byte[])(await command.ExecuteScalarAsync())!;

        Assert.Equal(32, hash.Length);
        Assert.DoesNotContain(key.Secret[(key.Secret.LastIndexOf('_') + 1)..], System.Text.Encoding.UTF8.GetString(hash), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Calls_over_the_limit_of_a_credential_are_answered_with_429_and_when_to_try_again()
    {
        var (_, owner) = await TenantAsync("Límite de llaves SAC");
        var key = await NewKeyAsync(owner, Roles.ReadOnly);

        var previous = Environment.GetEnvironmentVariable("RateLimiting__ApiPermitPerMinute");
        Environment.SetEnvironmentVariable("RateLimiting__ApiPermitPerMinute", "3");
        try
        {
            await using var limited = new WebApplicationFactory<Program>();
            using var client = limited.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
            using var other = limited.CreateClient();
            other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await NewKeyAsync(owner, Roles.ReadOnly, "Otra llave")).Secret);

            var statuses = new List<HttpStatusCode>();
            HttpResponseMessage? last = null;
            for (var i = 0; i < 5; i++)
            {
                last = await client.GetAsync("/api/v1/companies");
                statuses.Add(last.StatusCode);
            }

            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
            Assert.True(last!.Headers.RetryAfter is not null);
            Assert.Equal(SecureFact.SharedKernel.ErrorCodes.RateLimited, await CodeAsync(last));
            // The limit is of the credential: another key is not held back, and the health check is never limited.
            Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/v1/companies")).StatusCode);
            for (var i = 0; i < 10; i++)
            {
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("RateLimiting__ApiPermitPerMinute", previous);
        }
    }
}
