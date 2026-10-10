using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SecureFact.Audit.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Tenancy;

namespace SecureFact.Security.Tests;

/// <summary>Entering an account as support (ADR-069): the owner authorizes, the person who supports reads, and nothing else is possible.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class SupportAccessApiTests(ApiFixture api)
{
    private sealed record GrantRow(Guid Id, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, string? Note, string Status, int Entries);

    private sealed record SessionRow(string AccessToken, int ExpiresInSeconds, Guid TenantId, string TenantName, DateTimeOffset ExpiresAt, bool ReadOnly);

    private sealed record AvailableRow(Guid TenantId, string TenantName, Guid GrantId, DateTimeOffset ExpiresAt);

    private sealed record Created(Guid Id);

    private sealed record Account(Guid TenantId, string OwnerEmail, HttpClient Owner);

    private sealed record AuditRow(string Action, string ActorType, Guid? ActorUserId);

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private async Task<Account> NewAccountAsync(string name)
    {
        var tenantId = await api.CreateTenantAsync($"{name} {Guid.NewGuid():N}"[..28]);
        using var admin = await api.AdminClientAsync();
        var owner = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        return new Account(tenantId, owner.Email, api.ClientFor(await api.LoginOkAsync(owner.Email, owner.Password)));
    }

    private async Task<HttpClient> SupportStaffAsync()
    {
        using var admin = await api.AdminClientAsync();
        var staff = await ApiFixture.CreateUserAsync(admin, Roles.PlatformSupport, null);
        return api.ClientFor(await api.LoginOkAsync(staff.Email, staff.Password));
    }

    private static async Task<GrantRow> GrantAsync(Account account, int hours = 2, string? note = "Ayuda con la configuración")
    {
        var response = await account.Owner.PostAsJsonAsync("/api/v1/support-access", new { hours, note });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GrantRow>(ApiFixture.JsonOptions))!;
    }

    private static Task<HttpResponseMessage> EnterAsync(HttpClient staff, Guid tenantId, string reason = "Revisar por qué no ve sus series") =>
        staff.PostAsJsonAsync("/api/v1/support-sessions", new { tenantId, reason });

    private HttpClient WithToken(string token)
    {
        var client = api.NewClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Nobody_enters_an_account_that_did_not_authorize_it_not_even_the_super_admin()
    {
        var account = await NewAccountAsync("Sin autorización");
        var staff = await SupportStaffAsync();
        using var admin = await api.AdminClientAsync();

        var refused = await EnterAsync(staff, account.TenantId);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("SF-SUP-001", await CodeAsync(refused));
        Assert.Equal("SF-SUP-001", await CodeAsync(await EnterAsync(admin, account.TenantId)));
        Assert.Equal("SF-SUP-001", await CodeAsync(await EnterAsync(staff, Guid.NewGuid()))); // an account that does not exist answers the same

        // The people of the account do not enter anything, and the platform does not authorize in the name of the account.
        Assert.Equal(HttpStatusCode.Forbidden, (await EnterAsync(account.Owner, account.TenantId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/v1/support-access", new { hours = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync("/api/v1/support-access", new { hours = 1 })).StatusCode);
    }

    [Fact]
    public async Task Only_the_owner_authorizes_with_a_limited_time_one_at_a_time_and_a_note_that_is_not_too_long()
    {
        var account = await NewAccountAsync("Dueño autoriza");
        using var admin = await api.AdminClientAsync();
        var tenantAdmin = await ApiFixture.CreateUserAsync(admin, Roles.TenantAdmin, account.TenantId);
        using var adminOfAccount = api.ClientFor(await api.LoginOkAsync(tenantAdmin.Email, tenantAdmin.Password));
        Assert.Equal(HttpStatusCode.Forbidden, (await adminOfAccount.PostAsJsonAsync("/api/v1/support-access", new { hours = 1 })).StatusCode); // an administrator does not give the access to the data

        foreach (var body in new object[] { new { hours = 0 }, new { hours = 73 }, new { hours = 1, note = new string('x', 201) } })
        {
            var refused = await account.Owner.PostAsJsonAsync("/api/v1/support-access", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("SF-SUP-002", await CodeAsync(refused));
        }

        var grant = await GrantAsync(account, hours: 3);
        Assert.Equal(("Active", 0), (grant.Status, grant.Entries));
        Assert.InRange((grant.ExpiresAt - DateTimeOffset.UtcNow).TotalMinutes, 170, 181);
        var second = await account.Owner.PostAsJsonAsync("/api/v1/support-access", new { hours = 1 });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("SF-SUP-002", await CodeAsync(second));

        var listed = (await account.Owner.GetFromJsonAsync<List<GrantRow>>("/api/v1/support-access", ApiFixture.JsonOptions))!;
        Assert.Equal(grant.Id, Assert.Single(listed).Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await adminOfAccount.GetAsync("/api/v1/support-access")).StatusCode);

        // The authorization is audited, as the other decisions of the account.
        var events = (await account.Owner.GetFromJsonAsync<List<AuditRow>>("/api/v1/audit?take=50", ApiFixture.JsonOptions))!;
        Assert.Contains(events, e => e.Action == AuditActions.SupportAccessGranted);
    }

    [Fact]
    public async Task With_the_authorization_the_support_enters_reads_the_account_and_changes_nothing()
    {
        var account = await NewAccountAsync("Entrada de soporte");
        var other = await NewAccountAsync("Otra cuenta");
        var staff = await SupportStaffAsync();
        var grant = await GrantAsync(account);

        var available = (await staff.GetFromJsonAsync<List<AvailableRow>>("/api/v1/support-sessions/available", ApiFixture.JsonOptions))!;
        Assert.Contains(available, a => a.TenantId == account.TenantId && a.GrantId == grant.Id);
        Assert.DoesNotContain(available, a => a.TenantId == other.TenantId);

        Assert.Equal("SF-SUP-002", await CodeAsync(await EnterAsync(staff, account.TenantId, "  "))); // the reason is required: the owner reads it
        var entered = await EnterAsync(staff, account.TenantId, "Revisar por qué no ve sus series");
        Assert.Equal(HttpStatusCode.OK, entered.StatusCode);
        var session = (await entered.Content.ReadFromJsonAsync<SessionRow>(ApiFixture.JsonOptions))!;
        Assert.True(session.ReadOnly);
        Assert.Equal(account.TenantId, session.TenantId);
        Assert.InRange(session.ExpiresInSeconds, 1, 30 * 60);
        Assert.DoesNotContain("refresh", await entered.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase); // nothing renews it

        using var inside = WithToken(session.AccessToken);
        var users = await inside.GetStringAsync("/api/v1/users");
        Assert.Contains(account.OwnerEmail, users, StringComparison.Ordinal);
        Assert.DoesNotContain(other.OwnerEmail, users, StringComparison.Ordinal); // the data of the account and of no other
        Assert.Equal(HttpStatusCode.OK, (await inside.GetAsync("/api/v1/companies")).StatusCode);

        // It cannot change anything, whatever the endpoint asks for.
        foreach (var (method, path) in new[] { ("POST", "/api/v1/companies"), ("POST", "/api/v1/users"), ("POST", "/api/v1/auth/mfa/enroll"), ("POST", "/api/v1/support-access"), ("PUT", "/api/v1/companies/" + Guid.NewGuid()), ("DELETE", "/api/v1/users/" + Guid.NewGuid()) })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };
            var refused = await inside.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("SF-SUP-004", await CodeAsync(refused));
        }

        // What the role does not read is not read: the keys, the webhooks, the platform.
        Assert.Equal(HttpStatusCode.Forbidden, (await inside.GetAsync("/api/v1/api-keys")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await inside.GetAsync("/api/v1/support-access")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await inside.GetAsync("/api/v1/platform/tenants")).StatusCode);

        // The owner sees that someone entered, and the entry counts in the authorization.
        var listed = (await account.Owner.GetFromJsonAsync<List<GrantRow>>("/api/v1/support-access", ApiFixture.JsonOptions))!;
        Assert.Equal(1, Assert.Single(listed).Entries);
        var events = (await account.Owner.GetFromJsonAsync<List<AuditRow>>("/api/v1/audit?take=50", ApiFixture.JsonOptions))!;
        Assert.Contains(events, e => e.Action == AuditActions.SupportAccessEntered);

        // Ending the session is allowed, and it ends.
        Assert.Equal(HttpStatusCode.NoContent, (await inside.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await inside.GetAsync("/api/v1/users")).StatusCode);
    }

    [Fact]
    public async Task The_owners_are_told_each_time_someone_enters_with_the_reason_and_who()
    {
        var account = await NewAccountAsync("Aviso de entrada");
        var staff = await SupportStaffAsync();
        await GrantAsync(account);

        Assert.Equal(HttpStatusCode.OK, (await EnterAsync(staff, account.TenantId, "Revisar la serie F001")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnterAsync(staff, account.TenantId, "Segunda revisión")).StatusCode);

        await api.DrainMailAsync();
        var notices = api.Mail.To(account.OwnerEmail).Where(m => m.Subject == "Alguien de soporte entró a su cuenta").ToList();
        Assert.Equal(2, notices.Count);
        Assert.Contains(notices, m => m.Text.Contains("Revisar la serie F001", StringComparison.Ordinal) && m.Text.Contains("de soporte de la plataforma", StringComparison.Ordinal));
        Assert.All(notices, m => Assert.Contains($"{ApiFixture.PublicUrl}/soporte", m.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Taking_the_authorization_back_ends_the_open_sessions_and_closes_the_door()
    {
        var account = await NewAccountAsync("Quitar acceso");
        var other = await NewAccountAsync("Ajena al revoke");
        var staff = await SupportStaffAsync();
        var grant = await GrantAsync(account);
        var session = (await (await EnterAsync(staff, account.TenantId)).Content.ReadFromJsonAsync<SessionRow>(ApiFixture.JsonOptions))!;
        using var inside = WithToken(session.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await inside.GetAsync("/api/v1/users")).StatusCode);

        // Another account cannot take it back.
        var foreign = await other.Owner.PostAsync($"/api/v1/support-access/{grant.Id}/revoke", null);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("SF-SUP-003", await CodeAsync(foreign));
        Assert.Equal(HttpStatusCode.OK, (await inside.GetAsync("/api/v1/users")).StatusCode);

        var revoked = await account.Owner.PostAsync($"/api/v1/support-access/{grant.Id}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal("Revoked", (await revoked.Content.ReadFromJsonAsync<GrantRow>(ApiFixture.JsonOptions))!.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await inside.GetAsync("/api/v1/users")).StatusCode); // at once, not when the token expires
        Assert.Equal("SF-SUP-001", await CodeAsync(await EnterAsync(staff, account.TenantId)));
        var again = await account.Owner.PostAsync($"/api/v1/support-access/{grant.Id}/revoke", null); // taking it back twice changes nothing
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        // A new authorization can be given afterwards.
        await GrantAsync(account);
        Assert.Equal(HttpStatusCode.OK, (await EnterAsync(staff, account.TenantId)).StatusCode);
    }

    [Fact]
    public async Task The_session_never_outlives_the_authorization_and_an_expired_one_opens_nothing()
    {
        var account = await NewAccountAsync("Vencimiento");
        var staff = await SupportStaffAsync();
        var grant = await GrantAsync(account, hours: 1);

        using (api.Clock.At(DateTimeOffset.UtcNow.AddMinutes(50)))
        {
            var near = (await (await EnterAsync(staff, account.TenantId)).Content.ReadFromJsonAsync<SessionRow>(ApiFixture.JsonOptions))!;
            Assert.True(near.ExpiresAt <= grant.ExpiresAt); // ten minutes were left of the hour, not the thirty of a session
            Assert.InRange(near.ExpiresInSeconds, 1, 11 * 60);
        }

        using (api.Clock.At(DateTimeOffset.UtcNow.AddMinutes(90)))
        {
            Assert.Equal("SF-SUP-001", await CodeAsync(await EnterAsync(staff, account.TenantId)));
            var listed = (await account.Owner.GetFromJsonAsync<List<GrantRow>>("/api/v1/support-access", ApiFixture.JsonOptions))!;
            Assert.Equal("Expired", Assert.Single(listed).Status);
        }
    }

    [Fact]
    public async Task A_suspended_account_is_not_entered_and_the_support_role_cannot_be_given_to_anyone()
    {
        var account = await NewAccountAsync("Suspendida");
        var staff = await SupportStaffAsync();
        using var admin = await api.AdminClientAsync();
        await GrantAsync(account);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{account.TenantId}/status", new { status = "Suspended", reason = "Prueba de acceso de soporte" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await EnterAsync(staff, account.TenantId)).StatusCode);

        var account2 = await NewAccountAsync("Rol interno");
        var made = await account2.Owner.PostAsJsonAsync("/api/v1/users", new { email = $"{Guid.NewGuid():N}@securefact.test", displayName = "Intruso", password = ApiFixture.StrongPassword, roles = new[] { Roles.SupportViewer } });
        Assert.NotEqual(HttpStatusCode.Created, made.StatusCode);
        var key = await account2.Owner.PostAsJsonAsync("/api/v1/api-keys", new { name = "Llave de soporte", role = Roles.SupportViewer });
        Assert.NotEqual(HttpStatusCode.Created, key.StatusCode);
    }

    [Fact]
    public async Task A_reseller_enters_only_its_own_customers_and_only_with_their_authorization()
    {
        using var admin = await api.AdminClientAsync();
        var created = await admin.PostAsJsonAsync("/api/v1/platform/resellers", new { name = $"Canal {Guid.NewGuid():N}"[..18] });
        var resellerId = (await created.Content.ReadFromJsonAsync<Created>(ApiFixture.JsonOptions))!.Id;
        var email = $"{Guid.NewGuid():N}@canal.test";
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "Canal", password = ApiFixture.StrongPassword, roles = new[] { Roles.ResellerAdmin }, resellerId })).StatusCode);
        using var reseller = api.ClientFor(await api.LoginOkAsync(email, ApiFixture.StrongPassword));

        var opened = await reseller.PostAsJsonAsync("/api/v1/reseller/tenants", new { name = $"Cliente {Guid.NewGuid():N}"[..16], environment = "Sandbox" });
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var customerId = (await opened.Content.ReadFromJsonAsync<Created>(ApiFixture.JsonOptions))!.Id;
        var customerOwner = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, customerId);
        var customer = new Account(customerId, customerOwner.Email, api.ClientFor(await api.LoginOkAsync(customerOwner.Email, customerOwner.Password)));
        var stranger = await NewAccountAsync("Cliente de otro");

        await GrantAsync(stranger);
        Assert.Equal("SF-SUP-001", await CodeAsync(await EnterAsync(reseller, customerId))); // its customer did not authorize yet
        await GrantAsync(customer);

        var available = (await reseller.GetFromJsonAsync<List<AvailableRow>>("/api/v1/support-sessions/available", ApiFixture.JsonOptions))!;
        Assert.Equal(customerId, Assert.Single(available).TenantId); // the authorization of an account that is not its customer is not even listed
        Assert.Equal("SF-SUP-001", await CodeAsync(await EnterAsync(reseller, stranger.TenantId))); // nor does it open
        var entered = await EnterAsync(reseller, customerId, "Capacitación del cliente");
        Assert.Equal(HttpStatusCode.OK, entered.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await EnterAsync(customer.Owner, customerId)).StatusCode);

        await api.DrainMailAsync();
        Assert.Contains(api.Mail.To(customer.OwnerEmail), m => m.Subject == "Alguien de soporte entró a su cuenta" && m.Text.Contains("de su revendedor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task What_a_person_does_inside_an_account_is_audited_as_support_and_not_as_a_user_of_the_account()
    {
        var account = await NewAccountAsync("Auditoría de soporte");

        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UseTenant(new TenantId(account.TenantId));
        var supporter = Guid.NewGuid();
        var trail = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<SecureFact.Audit.Application.AuditTrail>(scope.ServiceProvider, (ICurrentUser)new SupportUser(supporter, account.TenantId));
        await trail.RecordAsync(new AuditEvent("test.support.read", "thing", "1", account.TenantId), CancellationToken.None);

        var events = (await account.Owner.GetFromJsonAsync<List<AuditRow>>("/api/v1/audit?take=50", ApiFixture.JsonOptions))!;
        var mine = Assert.Single(events, e => e.Action == "test.support.read");
        Assert.Equal(("support", (Guid?)supporter), (mine.ActorType, mine.ActorUserId));
    }

    private sealed class SupportUser(Guid userId, Guid tenantId) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId => userId;

        public Guid? SessionId => Guid.NewGuid();

        public TenantId? TenantId => new(tenantId);

        public bool IsPlatform => false;

        public Guid? ResellerId => null;

        public IReadOnlySet<string> Roles { get; } = new HashSet<string> { Identity.Contracts.Roles.SupportViewer };

        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();

        public bool HasPermission(string permission) => false;

        public bool IsSupportAccess => true;
    }
}
