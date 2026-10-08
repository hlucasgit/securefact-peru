using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SecureFact.Identity.Contracts;
using SecureFact.Notifications.Application;
using SecureFact.Notifications.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>The notices by e-mail (ADR-054): who gets them, what they say and do not say, that the queue retries and gives up, and that the queue is the platform's alone.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class NoticeEmailsApiTests(ApiFixture api)
{
    private sealed record Row(Guid Id, string Name);

    private sealed record Domain(string? Host, string Status, string? TxtName, string? TxtValue);

    private sealed record Setup(HttpClient Reseller, Guid ResellerId, string ResellerAdminEmail, Guid TenantId, string TenantName, string OwnerEmail);

    private async Task<Setup> NewSetupAsync(HttpClient admin, string? brand = "Distribuidora Aviso")
    {
        var created = await admin.PostAsJsonAsync("/api/v1/platform/resellers", new { name = $"Aviso {Guid.NewGuid():N}"[..18] });
        var reseller = (await created.Content.ReadFromJsonAsync<Row>(ApiFixture.JsonOptions))!;
        var adminEmail = $"{Guid.NewGuid():N}@reseller.test";
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/users", new { email = adminEmail, displayName = "Admin", password = ApiFixture.StrongPassword, roles = new[] { Roles.ResellerAdmin }, resellerId = reseller.Id })).StatusCode);
        var client = api.ClientFor(await api.LoginOkAsync(adminEmail, ApiFixture.StrongPassword));
        if (brand is not null)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/v1/reseller/branding", new { brandName = brand, primaryColor = "#0b5394", supportEmail = "ayuda@aviso.test" })).StatusCode);
        }

        var tenantName = $"Cliente {Guid.NewGuid():N}"[..20];
        var opened = await client.PostAsJsonAsync("/api/v1/reseller/tenants", new { name = tenantName, environment = "Sandbox" });
        var tenant = (await opened.Content.ReadFromJsonAsync<Row>(ApiFixture.JsonOptions))!;
        var ownerEmail = $"{Guid.NewGuid():N}@cliente.test";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/v1/reseller/tenants/{tenant.Id}/owner", new { email = ownerEmail, displayName = "Dueño Uno", password = ApiFixture.StrongPassword })).StatusCode);
        return new Setup(client, reseller.Id, adminEmail, tenant.Id, tenantName, ownerEmail);
    }

    private async Task<EmailMessage> SingleMailAsync(string email, Predicate<EmailMessage>? which = null)
    {
        await api.DrainMailAsync();
        return Assert.Single(api.Mail.To(email), which ?? (_ => true));
    }

    // ---------- accounts ----------

    [Fact]
    public async Task A_new_account_is_told_it_exists_with_the_brand_of_its_reseller_and_never_with_a_password()
    {
        using var admin = await api.AdminClientAsync();
        var setup = await NewSetupAsync(admin);

        var welcome = await SingleMailAsync(setup.OwnerEmail);

        Assert.Equal("Distribuidora Aviso", welcome.FromName);
        Assert.Equal("ayuda@aviso.test", welcome.ReplyTo);
        Assert.Contains("Se creó su cuenta en Distribuidora Aviso", welcome.Subject, StringComparison.Ordinal);
        Assert.Contains("Dueño Uno", welcome.Text, StringComparison.Ordinal);
        Assert.Contains($"{ApiFixture.PublicUrl}/ingresar", welcome.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFixture.StrongPassword, welcome.Text + welcome.Html, StringComparison.Ordinal);
        Assert.Contains("¿Olvidó su contraseña?", welcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_user_created_by_the_platform_gets_the_notice_of_the_platform()
    {
        using var admin = await api.AdminClientAsync();
        var tenantId = await api.CreateTenantAsync($"Aviso plataforma {Guid.NewGuid():N}"[..28]);
        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);

        var welcome = await SingleMailAsync(user.Email);

        Assert.Equal("SecureFact Perú", welcome.FromName);
        Assert.Contains($"{ApiFixture.PublicUrl}/ingresar", welcome.Text, StringComparison.Ordinal);
    }

    // ---------- accounts: status ----------

    [Fact]
    public async Task The_owners_are_told_when_their_account_is_suspended_reactivated_or_closed_without_the_reason()
    {
        using var admin = await api.AdminClientAsync();
        var setup = await NewSetupAsync(admin);
        var second = $"{Guid.NewGuid():N}@cliente.test";
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/users", new { email = second, displayName = "Dueño Dos", password = ApiFixture.StrongPassword, roles = new[] { Roles.TenantOwner }, tenantId = setup.TenantId })).StatusCode);
        const string reason = "Factura de marzo sin pagar, motivo interno";

        Assert.Equal(HttpStatusCode.OK, (await setup.Reseller.PostAsJsonAsync($"/api/v1/reseller/tenants/{setup.TenantId}/status", new { status = "Suspended", reason })).StatusCode);
        await api.DrainMailAsync();
        foreach (var owner in new[] { setup.OwnerEmail, second })
        {
            var suspended = Assert.Single(api.Mail.To(owner), m => m.Subject.Contains("suspendida", StringComparison.Ordinal));
            Assert.Contains($"«{setup.TenantName}»", suspended.Subject, StringComparison.Ordinal);
            Assert.Equal("Distribuidora Aviso", suspended.FromName);
            Assert.DoesNotContain("Factura de marzo", suspended.Text + suspended.Html, StringComparison.Ordinal);
            Assert.Contains("nadie de la cuenta puede ingresar", suspended.Text, StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.OK, (await setup.Reseller.PostAsJsonAsync($"/api/v1/reseller/tenants/{setup.TenantId}/status", new { status = "Active", reason })).StatusCode);
        var reactivated = await SingleMailAsync(setup.OwnerEmail, m => m.Subject.Contains("reactivada", StringComparison.Ordinal));
        Assert.Contains($"{ApiFixture.PublicUrl}/ingresar", reactivated.Text, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{setup.TenantId}/status", new { status = "Closed", reason })).StatusCode);
        var closed = await SingleMailAsync(setup.OwnerEmail, m => m.Subject.Contains("cerrada", StringComparison.Ordinal));
        Assert.Contains("de forma definitiva", closed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Factura de marzo", closed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_suspension_by_the_platform_is_told_too_and_an_account_without_owners_is_not_a_problem()
    {
        using var admin = await api.AdminClientAsync();
        var setup = await NewSetupAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{setup.TenantId}/status", new { status = "Suspended", reason = "Uso indebido investigado" })).StatusCode);

        var suspended = await SingleMailAsync(setup.OwnerEmail, m => m.Subject.Contains("suspendida", StringComparison.Ordinal));
        Assert.DoesNotContain("Uso indebido", suspended.Text, StringComparison.Ordinal);

        var empty = await api.CreateTenantAsync($"Sin dueños {Guid.NewGuid():N}"[..24]);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{empty}/status", new { status = "Suspended", reason = "Cuenta sin dueños" })).StatusCode);
    }

    // ---------- domains ----------

    [Fact]
    public async Task The_administrators_of_a_reseller_are_told_when_a_domain_is_assigned_verified_and_lost_and_not_when_it_is_cleared()
    {
        using var admin = await api.AdminClientAsync();
        var setup = await NewSetupAsync(admin);
        var host = $"aviso-{Guid.NewGuid():N}"[..20] + ".e2e.test";

        var assigned = await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{setup.ResellerId}/host", new { host });
        var domain = (await assigned.Content.ReadFromJsonAsync<Domain>(ApiFixture.JsonOptions))!;
        var pending = await SingleMailAsync(setup.ResellerAdminEmail, m => m.Subject.Contains("Se asignó el dominio", StringComparison.Ordinal));
        Assert.Contains(host, pending.Subject, StringComparison.Ordinal);
        Assert.Contains($"{ApiFixture.PublicUrl}/revendedor/marca", pending.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(domain.TxtValue!, pending.Text + pending.Html, StringComparison.Ordinal); // the proof is shown on the screen, not sent

        api.Dns.PublishTxt(domain.TxtName!, domain.TxtValue!);
        api.Dns.PointAt(host, FakeDomainNameSystem.EdgeHost);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/platform/resellers/{setup.ResellerId}/domain/verify", null)).StatusCode);
        var verified = await SingleMailAsync(setup.ResellerAdminEmail, m => m.Subject.Contains("quedó verificado", StringComparison.Ordinal));
        Assert.Contains($"https://{host}/ingresar", verified.Text, StringComparison.Ordinal);

        api.Dns.Unpublish(domain.TxtName!);
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(2100);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/platform/resellers/{setup.ResellerId}/domain/verify", null)).StatusCode);
        }

        var lost = await SingleMailAsync(setup.ResellerAdminEmail, m => m.Subject.Contains("dejó de responder", StringComparison.Ordinal));
        Assert.Contains("dejó de mostrar su marca", lost.Text, StringComparison.Ordinal);

        var before = (await SnapshotCountAsync(setup.ResellerAdminEmail));
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/platform/resellers/{setup.ResellerId}/host", new { host = (string?)null })).StatusCode);
        await api.DrainMailAsync();
        Assert.Equal(before, api.Mail.To(setup.ResellerAdminEmail).Count);
    }

    private async Task<int> SnapshotCountAsync(string email)
    {
        await api.DrainMailAsync();
        return api.Mail.To(email).Count;
    }

    // ---------- the queue ----------

    private async Task<Guid> EnqueueAsync(string subject)
    {
        await using var scope = api.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IEmailOutbox>().EnqueueAsync(new EmailMessage($"{Guid.NewGuid():N}@cola.test", subject, "texto del aviso", "<p>texto del aviso</p>"), CancellationToken.None));
        return await api.Postgres.ScalarAsOwnerAsync<Guid>($"SELECT id FROM notifications.email_queue WHERE subject = '{subject}'");
    }

    [Fact]
    public async Task A_failed_delivery_is_retried_later_with_backoff_and_the_text_is_dropped_once_it_is_sent()
    {
        await api.DrainMailAsync();
        var subject = $"Reintento {Guid.NewGuid():N}";
        var id = await EnqueueAsync(subject);

        api.Mail.Fail = true;
        try
        {
            var failed = await api.DispatchMailOnceAsync();
            Assert.True(failed.Failed >= 1);
        }
        finally
        {
            api.Mail.Fail = false;
        }

        Assert.Equal(1, await api.Postgres.ScalarAsOwnerAsync<int>($"SELECT attempts FROM notifications.email_queue WHERE id = '{id}'"));
        Assert.Contains("simulated", await api.Postgres.ScalarAsOwnerAsync<string>($"SELECT last_error FROM notifications.email_queue WHERE id = '{id}'"), StringComparison.Ordinal);
        Assert.Equal(0, (await api.DispatchMailOnceAsync()).Sent); // the backoff: it is not due yet

        await api.Postgres.ExecuteAsOwnerAsync($"UPDATE notifications.email_queue SET next_attempt_at = now() - interval '1 hour' WHERE id = '{id}'");
        Assert.True((await api.DispatchMailOnceAsync()).Sent >= 1);

        Assert.Equal(string.Empty, await api.Postgres.ScalarAsOwnerAsync<string>($"SELECT text_body || html_body FROM notifications.email_queue WHERE id = '{id}'"));
        Assert.True(await api.Postgres.ScalarAsOwnerAsync<bool>($"SELECT sent_at IS NOT NULL FROM notifications.email_queue WHERE id = '{id}'"));
    }

    [Fact]
    public async Task An_email_that_keeps_failing_dies_at_the_limit_and_is_not_tried_again()
    {
        await api.DrainMailAsync();
        var id = await EnqueueAsync($"Muerto {Guid.NewGuid():N}");
        await api.Postgres.ExecuteAsOwnerAsync($"UPDATE notifications.email_queue SET attempts = 9 WHERE id = '{id}'");

        api.Mail.Fail = true;
        try
        {
            var report = await api.DispatchMailOnceAsync();
            Assert.True(report.Dead >= 1);
        }
        finally
        {
            api.Mail.Fail = false;
        }

        Assert.True(await api.Postgres.ScalarAsOwnerAsync<bool>($"SELECT dead_at IS NOT NULL FROM notifications.email_queue WHERE id = '{id}'"));
        await api.Postgres.ExecuteAsOwnerAsync($"UPDATE notifications.email_queue SET next_attempt_at = now() - interval '1 hour' WHERE id = '{id}'");
        await api.DrainMailAsync();
        Assert.Equal(10, await api.Postgres.ScalarAsOwnerAsync<int>($"SELECT attempts FROM notifications.email_queue WHERE id = '{id}'")); // nobody picked it up again
    }

    [Fact]
    public async Task What_was_sent_or_died_long_ago_is_purged_and_what_is_pending_is_never()
    {
        await api.DrainMailAsync();
        var sent = await EnqueueAsync($"Antiguo enviado {Guid.NewGuid():N}");
        var pending = await EnqueueAsync($"Antiguo pendiente {Guid.NewGuid():N}");
        await api.Postgres.ExecuteAsOwnerAsync($"UPDATE notifications.email_queue SET sent_at = now() - interval '40 days', text_body = '', html_body = '' WHERE id = '{sent}'");
        await api.Postgres.ExecuteAsOwnerAsync($"UPDATE notifications.email_queue SET created_at = now() - interval '40 days', next_attempt_at = now() + interval '1 day' WHERE id = '{pending}'");

        await using var scope = api.Services.CreateAsyncScope();
        var removed = await scope.ServiceProvider.GetRequiredService<IEmailDispatcher>().PurgeAsync(TimeSpan.FromDays(30), CancellationToken.None);

        Assert.True(removed >= 1);
        Assert.Equal(0, await api.Postgres.ScalarAsOwnerAsync<int>($"SELECT count(*)::int FROM notifications.email_queue WHERE id = '{sent}'"));
        Assert.Equal(1, await api.Postgres.ScalarAsOwnerAsync<int>($"SELECT count(*)::int FROM notifications.email_queue WHERE id = '{pending}'"));
    }

    [Fact]
    public async Task The_queue_is_invisible_outside_the_platform_scope()
    {
        await EnqueueAsync($"Privado {Guid.NewGuid():N}");

        await using var connection = new NpgsqlConnection(api.Postgres.AppConnectionString);
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM notifications.email_queue", connection);
        Assert.Equal(0L, await count.ExecuteScalarAsync()); // the runtime role without the platform scope sees no row

        await using (var scope = new NpgsqlCommand("SELECT set_config('app.scope', 'tenant', false)", connection))
        {
            await scope.ExecuteNonQueryAsync();
        }

        await using var asTenant = new NpgsqlCommand("SELECT count(*) FROM notifications.email_queue", connection);
        Assert.Equal(0L, await asTenant.ExecuteScalarAsync());
    }
}
