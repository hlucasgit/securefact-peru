using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecureFact.Identity.Application;
using SecureFact.Identity.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>End-to-end authentication, session and RBAC behaviour over HTTP against a real PostgreSQL.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class IdentityApiTests(ApiFixture api)
{
    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private async Task<(Guid TenantId, HttpClient Owner, TestUser OwnerUser)> NewTenantWithOwnerAsync(string name)
    {
        var tenantId = await api.CreateTenantAsync(name);
        using var admin = await api.AdminClientAsync();
        var owner = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        return (tenantId, api.ClientFor(await api.LoginOkAsync(owner.Email, owner.Password)), owner);
    }

    // ---- authentication ----

    [Fact]
    public async Task Protected_endpoints_reject_anonymous_and_tampered_tokens()
    {
        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/users")).StatusCode);

        var tokens = await api.LoginOkAsync(ApiFixture.AdminEmail, ApiFixture.AdminPassword);
        using var tampered = api.ClientFor(tokens with { AccessToken = tokens.AccessToken[..^3] + "abc" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await tampered.GetAsync("/api/v1/users")).StatusCode);
    }

    [Fact]
    public async Task Unknown_account_and_wrong_password_are_indistinguishable()
    {
        var (_, _, owner) = await NewTenantWithOwnerAsync("Enumeration SAC");

        var wrongPassword = await api.LoginAsync(owner.Email, "definitely not the password");
        var unknown = await api.LoginAsync("nobody@securefact.test", "definitely not the password");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrongPassword.Content.ReadAsStringAsync().ContinueWith(t => Normalize(t.Result)),
                     await unknown.Content.ReadAsStringAsync().ContinueWith(t => Normalize(t.Result)));
    }

    private static string Normalize(string problemJson)
    {
        using var doc = JsonDocument.Parse(problemJson);
        return $"{doc.RootElement.GetProperty("code").GetString()}|{doc.RootElement.GetProperty("title").GetString()}|{doc.RootElement.GetProperty("detail").GetString()}";
    }

    [Fact]
    public async Task Account_locks_after_repeated_failures_even_for_the_correct_password()
    {
        var (_, _, owner) = await NewTenantWithOwnerAsync("Lockout SAC");

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await api.LoginAsync(owner.Email, "wrong password attempt")).StatusCode);
        }

        var response = await api.LoginAsync(owner.Email, owner.Password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("SF-AUTH-004", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Access_token_dies_immediately_when_the_session_is_revoked()
    {
        var tokens = await api.LoginOkAsync(ApiFixture.AdminEmail, ApiFixture.AdminPassword);
        using var client = api.ClientFor(tokens);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/users")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/auth/logout", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/users")).StatusCode);
    }

    // ---- refresh tokens ----

    [Fact]
    public async Task Refresh_rotates_the_token_and_reuse_revokes_the_whole_family()
    {
        var (_, _, owner) = await NewTenantWithOwnerAsync("Rotation SAC");
        var first = await api.LoginOkAsync(owner.Email, owner.Password);

        using var anonymous = api.NewClient();
        var rotated = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var second = (await rotated.Content.ReadFromJsonAsync<AuthTokens>(ApiFixture.JsonOptions))!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        using var newSession = api.ClientFor(second);
        Assert.Equal(HttpStatusCode.OK, (await newSession.GetAsync("/api/v1/users")).StatusCode);

        // Presenting the already-rotated token again signals theft: the family, including the live session, is revoked.
        var replay = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await newSession.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = second.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task Unknown_refresh_tokens_are_rejected()
    {
        using var anonymous = api.NewClient();

        var response = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = "not-a-real-token" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("SF-AUTH-007", await ProblemCodeAsync(response));
    }

    // ---- tenant isolation through the API ----

    [Fact]
    public async Task A_tenant_owner_sees_only_its_own_users_and_cannot_touch_another_tenants()
    {
        var (tenantA, ownerA, _) = await NewTenantWithOwnerAsync("Isolation A SAC");
        var (tenantB, ownerB, ownerBUser) = await NewTenantWithOwnerAsync("Isolation B SAC");

        var list = (await ownerA.GetFromJsonAsync<List<UserDto>>("/api/v1/users", ApiFixture.JsonOptions))!;
        Assert.All(list, u => Assert.Equal(tenantA, u.TenantId));
        Assert.DoesNotContain(list, u => u.Id == ownerBUser.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await ownerA.GetAsync($"/api/v1/users/{ownerBUser.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerA.PostAsync($"/api/v1/users/{ownerBUser.Id}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerA.PostAsync($"/api/v1/users/{ownerBUser.Id}/sessions/revoke", null)).StatusCode);

        var crossTenantCreate = await ownerA.PostAsJsonAsync("/api/v1/users", new
        {
            email = $"{Guid.NewGuid():N}@securefact.test",
            displayName = "Intruder",
            password = ApiFixture.StrongPassword,
            roles = new[] { Roles.ReadOnly },
            tenantId = tenantB,
        });
        Assert.Equal(HttpStatusCode.Forbidden, crossTenantCreate.StatusCode);

        // B's data is untouched and still reachable by B.
        Assert.Equal(HttpStatusCode.OK, (await ownerB.GetAsync($"/api/v1/users/{ownerBUser.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_tenant_sees_only_its_own_tenant_record()
    {
        var (tenantA, ownerA, _) = await NewTenantWithOwnerAsync("Current A SAC");
        var (tenantB, _, _) = await NewTenantWithOwnerAsync("Current B SAC");

        var current = await ownerA.GetAsync("/api/v1/tenants/current");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        using (var body = JsonDocument.Parse(await current.Content.ReadAsStringAsync()))
        {
            Assert.Equal(tenantA.ToString(), body.RootElement.GetProperty("id").GetString());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await ownerA.GetAsync($"/api/v1/platform/tenants/{tenantB}")).StatusCode);
    }

    // ---- RBAC / privilege escalation ----

    [Fact]
    public async Task Tenant_users_cannot_create_tenants_or_platform_users()
    {
        var (_, owner, _) = await NewTenantWithOwnerAsync("No Platform SAC");

        var createTenant = await owner.PostAsJsonAsync("/api/v1/platform/tenants", new { name = "Rogue SAC", environment = "Production" });
        Assert.Equal(HttpStatusCode.Forbidden, createTenant.StatusCode);

        var createPlatformUser = await owner.PostAsJsonAsync("/api/v1/users", new
        {
            email = $"{Guid.NewGuid():N}@securefact.test",
            displayName = "Rogue Admin",
            password = ApiFixture.StrongPassword,
            roles = new[] { Roles.PlatformSuperAdmin },
        });
        Assert.Equal(HttpStatusCode.Forbidden, createPlatformUser.StatusCode);
    }

    [Fact]
    public async Task A_role_without_user_management_permission_is_denied()
    {
        var (tenantId, owner, _) = await NewTenantWithOwnerAsync("Read Only SAC");
        var readOnly = await ApiFixture.CreateUserAsync(owner, Roles.ReadOnly, tenantId);
        using var client = api.ClientFor(await api.LoginOkAsync(readOnly.Email, readOnly.Password));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/users")).StatusCode);
        var create = await client.PostAsJsonAsync("/api/v1/users", new
        {
            email = $"{Guid.NewGuid():N}@securefact.test",
            displayName = "Nope",
            password = ApiFixture.StrongPassword,
            roles = new[] { Roles.ReadOnly },
        });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task A_tenant_admin_cannot_grant_a_role_stronger_than_its_own()
    {
        var (tenantId, owner, _) = await NewTenantWithOwnerAsync("Escalation SAC");
        var admin = await ApiFixture.CreateUserAsync(owner, Roles.TenantAdmin, tenantId);
        using var adminClient = api.ClientFor(await api.LoginOkAsync(admin.Email, admin.Password));
        var target = await ApiFixture.CreateUserAsync(owner, Roles.ReadOnly, tenantId);

        var escalate = await adminClient.PostAsJsonAsync($"/api/v1/users/{target.Id}/roles", new { role = Roles.TenantOwner });
        Assert.Equal(HttpStatusCode.Forbidden, escalate.StatusCode);
        Assert.Equal("SF-USR-004", await ProblemCodeAsync(escalate));

        var allowed = await adminClient.PostAsJsonAsync($"/api/v1/users/{target.Id}/roles", new { role = Roles.Accountant });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task The_last_tenant_owner_cannot_lose_the_owner_role()
    {
        var (_, owner, ownerUser) = await NewTenantWithOwnerAsync("Last Owner SAC");
        var other = await ApiFixture.CreateUserAsync(owner, Roles.TenantAdmin, (await owner.GetFromJsonAsync<UserDto>($"/api/v1/users/{ownerUser.Id}", ApiFixture.JsonOptions))!.TenantId);
        Assert.NotEqual(Guid.Empty, other.Id);

        var assignSecondRole = await owner.PostAsJsonAsync($"/api/v1/users/{ownerUser.Id}/roles", new { role = Roles.Auditor });
        Assert.Equal(HttpStatusCode.OK, assignSecondRole.StatusCode);

        var removal = await owner.DeleteAsync($"/api/v1/users/{ownerUser.Id}/roles/{Roles.TenantOwner}");

        Assert.Equal(HttpStatusCode.Conflict, removal.StatusCode);
    }

    [Fact]
    public async Task Weak_passwords_and_duplicate_emails_are_rejected()
    {
        var (tenantId, owner, ownerUser) = await NewTenantWithOwnerAsync("Validation SAC");

        var weak = await owner.PostAsJsonAsync("/api/v1/users", new
        {
            email = $"{Guid.NewGuid():N}@securefact.test",
            displayName = "Weak",
            password = "short",
            roles = new[] { Roles.ReadOnly },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, weak.StatusCode);
        Assert.Equal("SF-AUTH-008", await ProblemCodeAsync(weak));

        var duplicate = await owner.PostAsJsonAsync("/api/v1/users", new
        {
            email = ownerUser.Email.ToUpperInvariant(),
            displayName = "Duplicate",
            password = ApiFixture.StrongPassword,
            roles = new[] { Roles.ReadOnly },
            tenantId,
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Deactivating_a_user_revokes_its_sessions_and_blocks_login()
    {
        var (tenantId, owner, _) = await NewTenantWithOwnerAsync("Deactivate SAC");
        var member = await ApiFixture.CreateUserAsync(owner, Roles.ReadOnly, tenantId);
        using var memberClient = api.ClientFor(await api.LoginOkAsync(member.Email, member.Password));
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.GetAsync("/api/v1/users")).StatusCode); // authenticated, just not authorised

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/users/{member.Id}/deactivate", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await memberClient.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.LoginAsync(member.Email, member.Password)).StatusCode);
    }

    // ---- MFA ----

    [Fact]
    public async Task Mfa_enrolment_then_login_requires_a_valid_non_replayed_code()
    {
        var (_, _, owner) = await NewTenantWithOwnerAsync("Mfa SAC");
        var tokens = await api.LoginOkAsync(owner.Email, owner.Password);
        using var client = api.ClientFor(tokens);

        var enrol = await client.PostAsync("/api/v1/auth/mfa/enroll", null);
        Assert.Equal(HttpStatusCode.OK, enrol.StatusCode);
        var enrolment = (await enrol.Content.ReadFromJsonAsync<MfaEnrollment>(ApiFixture.JsonOptions))!;
        Assert.StartsWith("otpauth://totp/", enrolment.OtpAuthUri, StringComparison.Ordinal);
        var secret = Base32Decode(enrolment.Secret);

        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var confirmCode = Totp.Compute(secret, step);
        var confirm = await client.PostAsJsonAsync("/api/v1/auth/mfa/confirm", new { code = confirmCode });
        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);

        var withoutCode = await api.LoginAsync(owner.Email, owner.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, withoutCode.StatusCode);
        Assert.Equal("SF-AUTH-005", await ProblemCodeAsync(withoutCode));

        // The code used to confirm enrolment is already consumed; a replay in the same window is refused.
        var replay = await api.LoginAsync(owner.Email, owner.Password, confirmCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("SF-AUTH-006", await ProblemCodeAsync(replay));

        var nextCode = Totp.Compute(secret, step + 1);
        Assert.Equal(HttpStatusCode.OK, (await api.LoginAsync(owner.Email, owner.Password, nextCode)).StatusCode);
    }

    private static byte[] Base32Decode(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var c in value)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }

    // ---- password reset ----

    [Fact]
    public async Task Password_reset_issues_a_single_use_token_and_revokes_sessions()
    {
        var (_, _, owner) = await NewTenantWithOwnerAsync("Reset SAC");
        var oldTokens = await api.LoginOkAsync(owner.Email, owner.Password);
        using var oldSession = api.ClientFor(oldTokens);
        using var anonymous = api.NewClient();

        Assert.Equal(HttpStatusCode.NoContent,
            (await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = owner.Email })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = "ghost@securefact.test" })).StatusCode);

        var token = api.Notifier.TokenFor(owner.Email);
        Assert.NotNull(token);
        Assert.Null(api.Notifier.TokenFor("ghost@securefact.test"));

        var newPassword = "Another long passphrase 2027";
        var confirm = await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { token, newPassword });
        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await oldSession.GetAsync("/api/v1/tenants/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.LoginAsync(owner.Email, owner.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.LoginAsync(owner.Email, newPassword)).StatusCode);

        var reuse = await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { token, newPassword = "Yet another long passphrase 2028" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reuse.StatusCode);
        Assert.Equal("SF-AUTH-009", await ProblemCodeAsync(reuse));
    }

    [Fact]
    public async Task Refresh_refuses_an_empty_or_unknown_token()
    {
        using var anonymous = api.NewClient();

        foreach (var token in new[] { "", "   ", "not-a-token-we-ever-issued" })
        {
            var response = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = token });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.StartsWith("SF-AUTH-", await ProblemCodeAsync(response), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Mfa_enrolment_refuses_a_confirmation_without_enrolment_a_wrong_code_and_a_second_enrolment()
    {
        var (_, _, owner) = await NewTenantWithOwnerAsync("Mfa Errors SAC");
        using var client = api.ClientFor(await api.LoginOkAsync(owner.Email, owner.Password));

        var early = await client.PostAsJsonAsync("/api/v1/auth/mfa/confirm", new { code = "123456" });
        Assert.Equal(HttpStatusCode.Unauthorized, early.StatusCode); // nothing to confirm yet
        Assert.Equal("SF-AUTH-006", await ProblemCodeAsync(early));

        var enrolment = (await (await client.PostAsync("/api/v1/auth/mfa/enroll", null)).Content.ReadFromJsonAsync<MfaEnrollment>(ApiFixture.JsonOptions))!;
        var secret = Base32Decode(enrolment.Secret);
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var valid = new[] { step - 1, step, step + 1 }.Select(s => Totp.Compute(secret, s)).ToHashSet(StringComparer.Ordinal);
        var wrong = Enumerable.Range(0, 1_000_000).Select(n => n.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)).First(c => !valid.Contains(c));

        var refused = await client.PostAsJsonAsync("/api/v1/auth/mfa/confirm", new { code = wrong });
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("SF-AUTH-006", await ProblemCodeAsync(refused));

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/mfa/confirm", new { code = Totp.Compute(secret, step) })).StatusCode);
        var again = await client.PostAsync("/api/v1/auth/mfa/enroll", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode); // the second factor is already active
    }

    [Fact]
    public async Task A_password_reset_refuses_an_empty_token_and_a_weak_password_and_keeps_the_old_password()
    {
        var (_, _, owner) = await NewTenantWithOwnerAsync("Reset Errors SAC");
        using var anonymous = api.NewClient();

        var empty = await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { token = "", newPassword = "Another long passphrase 2029" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        var unknown = await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { token = "nothing-we-sent", newPassword = "Another long passphrase 2029" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = owner.Email });
        var token = api.Notifier.TokenFor(owner.Email)!;
        var weak = await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { token, newPassword = "short" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, weak.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.LoginAsync(owner.Email, owner.Password)).StatusCode); // the password did not change
    }

    // ---- data at rest ----

    [Fact]
    public async Task Secrets_are_never_stored_in_clear_text()
    {
        var (_, _, owner) = await NewTenantWithOwnerAsync("At Rest SAC");
        var tokens = await api.LoginOkAsync(owner.Email, owner.Password);

        var passwordStored = await api.Postgres.ScalarAsOwnerAsync<string>(
            $"SELECT password_hash FROM identity.app_user WHERE id = '{owner.Id}'");
        var refreshHits = await api.Postgres.ScalarAsOwnerAsync<long>(
            $"SELECT count(*) FROM identity.user_session WHERE refresh_hash = convert_to('{tokens.RefreshToken}', 'UTF8')");
        var hashLengths = await api.Postgres.ScalarAsOwnerAsync<long>(
            $"SELECT count(*) FROM identity.user_session WHERE user_id = '{owner.Id}' AND length(refresh_hash) = 32");

        Assert.StartsWith("pbkdf2-sha512$", passwordStored, StringComparison.Ordinal);
        Assert.DoesNotContain(owner.Password, passwordStored, StringComparison.Ordinal);
        Assert.Equal(0, refreshHits);
        Assert.True(hashLengths >= 1, "Refresh tokens must be stored as a 32-byte SHA-256 digest.");
    }

    [Fact]
    public async Task Logs_never_contain_passwords_tokens_or_credentials()
    {
        var (_, _, owner) = await NewTenantWithOwnerAsync("Logging SAC");
        const string wrongPassword = "this-wrong-password-must-not-be-logged";
        await api.LoginAsync(owner.Email, wrongPassword);
        var tokens = await api.LoginOkAsync(owner.Email, owner.Password);
        using var client = api.ClientFor(tokens);
        await client.GetAsync("/api/v1/users?secret=query-string-secret");
        using var anonymous = api.NewClient();
        await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken });
        await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = owner.Email });

        var logs = string.Join(Environment.NewLine, api.Logs.Snapshot());

        Assert.Contains("HTTP POST /api/v1/auth/login", logs, StringComparison.Ordinal);
        foreach (var secret in new[] { wrongPassword, owner.Password, tokens.AccessToken, tokens.RefreshToken, "query-string-secret", api.Notifier.TokenFor(owner.Email)! })
        {
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        }
    }
}
