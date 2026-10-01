using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SecureFact.Audit.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Domain;

namespace SecureFact.Security.Tests;

/// <summary>Tamper-evidence, append-only enforcement, isolation and secret hygiene of the audit trail.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class AuditTests(ApiFixture api)
{
    private async Task<(Guid TenantId, HttpClient Owner, TestUser OwnerUser)> NewTenantAsync(string name)
    {
        var tenantId = await api.CreateTenantAsync(name);
        using var admin = await api.AdminClientAsync();
        var owner = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        return (tenantId, api.ClientFor(await api.LoginOkAsync(owner.Email, owner.Password)), owner);
    }

    private async Task AppendAsync(Guid tenantId, string action, int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            await using var scope = api.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAuditTrail>()
                .RecordAsync(new AuditEvent(action, "test", i.ToString(System.Globalization.CultureInfo.InvariantCulture), tenantId), CancellationToken.None);
        }
    }

    private async Task<AuditVerification> VerifyAsPlatformAsync(Guid tenantId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UsePlatform("audit verification test");
        return await scope.ServiceProvider.GetRequiredService<IAuditQuery>().VerifyAsync(tenantId, CancellationToken.None);
    }

    [Fact]
    public async Task Business_actions_leave_audit_events_visible_to_the_tenant_that_owns_them()
    {
        var (tenantId, owner, ownerUser) = await NewTenantAsync("Audit Visible SAC");
        var member = await ApiFixture.CreateUserAsync(owner, Roles.ReadOnly, tenantId);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v1/users/{member.Id}/roles", new { role = Roles.Accountant })).StatusCode);

        var events = (await owner.GetFromJsonAsync<List<AuditRecord>>("/api/v1/audit?take=200", ApiFixture.JsonOptions))!;

        Assert.Contains(events, e => e.Action == AuditActions.TenantCreated);
        Assert.Contains(events, e => e.Action == AuditActions.UserCreated && e.EntityId == ownerUser.Id.ToString("D"));
        Assert.Contains(events, e => e.Action == AuditActions.LoginSucceeded && e.ActorUserId == ownerUser.Id);
        var assigned = Assert.Single(events, e => e.Action == AuditActions.UserRoleAssigned);
        Assert.Equal(member.Id.ToString("D"), assigned.EntityId);
        Assert.Equal(ownerUser.Id, assigned.ActorUserId);
        Assert.False(string.IsNullOrEmpty(assigned.CorrelationId));
        Assert.All(events, e => Assert.Equal(tenantId, e.TenantId));
    }

    [Fact]
    public async Task A_tenant_cannot_read_another_tenants_audit_trail_even_by_asking_for_it()
    {
        var (_, ownerA, _) = await NewTenantAsync("Audit A SAC");
        var (tenantB, _, _) = await NewTenantAsync("Audit B SAC");

        var events = (await ownerA.GetFromJsonAsync<List<AuditRecord>>($"/api/v1/audit?tenantId={tenantB}&take=200", ApiFixture.JsonOptions))!;

        Assert.DoesNotContain(events, e => e.TenantId == tenantB);
        Assert.NotEmpty(events);
    }

    [Fact]
    public async Task Roles_without_audit_permission_are_denied()
    {
        var (tenantId, owner, _) = await NewTenantAsync("Audit Denied SAC");
        var member = await ApiFixture.CreateUserAsync(owner, Roles.ReadOnly, tenantId);
        using var client = api.ClientFor(await api.LoginOkAsync(member.Email, member.Password));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/audit/verify", null)).StatusCode);
    }

    [Fact]
    public async Task Failed_logins_and_lockouts_are_recorded_and_secrets_never_appear()
    {
        var (_, owner, ownerUser) = await NewTenantAsync("Audit Failures SAC");
        for (var i = 0; i < 5; i++)
        {
            await api.LoginAsync(ownerUser.Email, "wrong password attempt");
        }

        var events = (await owner.GetFromJsonAsync<List<AuditRecord>>("/api/v1/audit?take=200", ApiFixture.JsonOptions))!;

        Assert.Equal(5, events.Count(e => e.Action == AuditActions.LoginFailed));
        Assert.Single(events, e => e.Action == AuditActions.AccountLocked);

        var leaks = await api.Postgres.ScalarAsOwnerAsync<long>($"""
            SELECT count(*) FROM audit.audit_event
            WHERE coalesce(old_values, '') || coalesce(new_values, '') ILIKE '%{ApiFixture.StrongPassword}%'
               OR coalesce(old_values, '') || coalesce(new_values, '') ILIKE '%wrong password attempt%'
               OR coalesce(old_values, '') || coalesce(new_values, '') ILIKE '%pbkdf2%'
            """);
        Assert.Equal(0, leaks);
    }

    [Fact]
    public async Task The_chain_is_contiguous_and_verifies_even_with_concurrent_writers()
    {
        var tenantId = await api.CreateTenantAsync("Audit Concurrency SAC");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => AppendAsync(tenantId, "test.concurrent", count: 5)));

        var result = await VerifyAsPlatformAsync(tenantId);
        Assert.True(result.IsIntact, result.Reason);
        Assert.Equal(41, result.EventsChecked); // 40 appended + the tenant.created event

        var sequences = await api.Postgres.ScalarAsOwnerAsync<string>(
            $"SELECT string_agg(seq::text, ',' ORDER BY seq) FROM audit.audit_event WHERE chain_key = '{tenantId}'");
        Assert.Equal(string.Join(',', Enumerable.Range(1, 41)), sequences);
    }

    [Fact]
    public async Task Verification_endpoint_reports_an_intact_chain()
    {
        var (_, owner, _) = await NewTenantAsync("Audit Verify SAC");

        var response = await owner.PostAsync("/api/v1/audit/verify", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<AuditVerification>(ApiFixture.JsonOptions))!;
        Assert.True(result.IsIntact);
        Assert.True(result.EventsChecked >= 2);
    }

    // ---- append-only enforcement ----

    [Fact]
    public async Task The_runtime_role_can_neither_update_nor_delete_audit_events()
    {
        var tenantId = await api.CreateTenantAsync("Audit AppOnly SAC");
        await using var connection = new NpgsqlConnection(api.Postgres.AppConnectionString);
        await connection.OpenAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('app.scope', 'platform', false)", connection))
        {
            await scope.ExecuteNonQueryAsync();
        }

        foreach (var sql in new[] { "UPDATE audit.audit_event SET action = 'x'", "DELETE FROM audit.audit_event", "TRUNCATE audit.audit_event" })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        Assert.NotEqual(Guid.Empty, tenantId);
    }

    [Fact]
    public async Task Even_the_schema_owner_is_stopped_by_the_append_only_triggers()
    {
        var tenantId = await api.CreateTenantAsync("Audit Trigger SAC");

        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            api.Postgres.ExecuteAsOwnerAsync($"UPDATE audit.audit_event SET action = 'tampered' WHERE chain_key = '{tenantId}'"));
        var delete = await Assert.ThrowsAsync<PostgresException>(() =>
            api.Postgres.ExecuteAsOwnerAsync($"DELETE FROM audit.audit_event WHERE chain_key = '{tenantId}'"));

        Assert.Contains("append-only", update.MessageText, StringComparison.Ordinal);
        Assert.Contains("append-only", delete.MessageText, StringComparison.Ordinal);
    }

    // ---- tamper evidence (simulating an attacker who bypasses the triggers) ----

    private async Task TamperAsync(string sql)
    {
        await api.Postgres.ExecuteAsOwnerAsync("ALTER TABLE audit.audit_event DISABLE TRIGGER USER");
        try
        {
            await api.Postgres.ExecuteAsOwnerAsync(sql);
        }
        finally
        {
            await api.Postgres.ExecuteAsOwnerAsync("ALTER TABLE audit.audit_event ENABLE TRIGGER USER");
        }
    }

    [Fact]
    public async Task Modifying_an_event_breaks_verification_at_that_event()
    {
        var tenantId = await api.CreateTenantAsync("Audit Tamper SAC");
        await AppendAsync(tenantId, "test.tamper", count: 4);

        await TamperAsync($"UPDATE audit.audit_event SET action = 'tampered' WHERE chain_key = '{tenantId}' AND seq = 3");

        var result = await VerifyAsPlatformAsync(tenantId);
        Assert.False(result.IsIntact);
        Assert.Equal(3, result.FirstBrokenSequence);
        Assert.Contains("modified", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_an_event_breaks_verification()
    {
        var tenantId = await api.CreateTenantAsync("Audit Delete SAC");
        await AppendAsync(tenantId, "test.delete", count: 4);

        await TamperAsync($"DELETE FROM audit.audit_event WHERE chain_key = '{tenantId}' AND seq = 2");

        var result = await VerifyAsPlatformAsync(tenantId);
        Assert.False(result.IsIntact);
        Assert.Equal(2, result.FirstBrokenSequence);
    }

    [Fact]
    public async Task Rewriting_an_event_and_its_hash_still_breaks_the_link_of_the_next_event()
    {
        var tenantId = await api.CreateTenantAsync("Audit Rehash SAC");
        await AppendAsync(tenantId, "test.rehash", count: 3);

        // A smarter attacker recomputes nothing here (the hash is SHA-256 in application code), so overwrite with a forged hash.
        await TamperAsync($"UPDATE audit.audit_event SET action = 'forged', hash = decode(repeat('ab', 32), 'hex') WHERE chain_key = '{tenantId}' AND seq = 2");

        var result = await VerifyAsPlatformAsync(tenantId);
        Assert.False(result.IsIntact);
        Assert.Equal(2, result.FirstBrokenSequence);
    }

    [Fact]
    public async Task Refresh_token_reuse_is_audited()
    {
        var (_, owner, ownerUser) = await NewTenantAsync("Audit Reuse SAC");
        var first = await api.LoginOkAsync(ownerUser.Email, ownerUser.Password);
        using var anonymous = api.NewClient();
        await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.RefreshToken });
        await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.RefreshToken });

        var events = (await owner.GetFromJsonAsync<List<AuditRecord>>("/api/v1/audit?take=200", ApiFixture.JsonOptions))!;

        Assert.Contains(events, e => e.Action == AuditActions.RefreshReuseDetected && e.ActorUserId == ownerUser.Id);
    }
}
