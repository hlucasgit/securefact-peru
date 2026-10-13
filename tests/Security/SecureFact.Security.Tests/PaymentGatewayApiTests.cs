using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SecureFact.Audit.Contracts;
using SecureFact.Identity.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Subscriptions.Application;
using SecureFact.Subscriptions.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>Paying a charge with a card through the gateway (ADR-073), with the simulator: the order of what is written, what is never charged twice, and who may do what.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class PaymentGatewayApiTests(ApiFixture api)
{
    private sealed record PlanRow(Guid Id);

    private sealed record ChargeRow(Guid Id, Guid TenantId, DateOnly Period, decimal TotalAmount, decimal PaidAmount, decimal Balance, string Status, DateOnly DueOn);

    private sealed record Account(Guid TenantId, TestUser Owner, HttpClient Client, ChargeRow Charge);

    private sealed record SettingsRow(string Provider, string? PublicKey, bool HasSecretKey, string Environment, bool Enabled);

    private sealed record ConfigRow(bool Enabled, string? Provider, string? PublicKey, string? Environment);

    private sealed class OwnerUser(Guid userId, Guid tenantId) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId => userId;

        public Guid? SessionId => Guid.NewGuid();

        public TenantId? TenantId => new(tenantId);

        public bool IsPlatform => false;

        public Guid? ResellerId => null;

        public IReadOnlySet<string> Roles { get; } = new HashSet<string> { Identity.Contracts.Roles.TenantOwner };

        public IReadOnlySet<string> Permissions { get; } = RoleCatalog.PermissionsOf(Identity.Contracts.Roles.TenantOwner);

        public bool HasPermission(string permission) => Permissions.Contains(permission);
    }

    private static string Token(string kind, string suffix = "") => "tkn_test_" + (kind + new string('0', 16)).PadRight(16, '0')[..(16 - suffix.Length)] + suffix;

    private static DateOnly NextMonth(int months = 1) => LimaCalendar.MonthStart(LimaCalendar.Today(DateTimeOffset.UtcNow)).AddMonths(months);

    private static DateTimeOffset At(int monthsAhead, int day) => LimaCalendar.StartOf(NextMonth(monthsAhead).AddDays(day - 1)).AddHours(12);

    private async Task UseSandboxAsync(bool enabled = true)
    {
        using var admin = await api.AdminClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/platform/payment-gateway", new { provider = "Sandbox", enabled })).StatusCode);
    }

    private async Task<Account> ChargedAsync(string name, decimal fee = 100m)
    {
        using var admin = await api.AdminClientAsync();
        var plan = (await (await admin.PostAsJsonAsync("/api/v1/platform/plans", new { code = $"pg-{Guid.NewGuid():N}"[..16], name = "Plan con pago en línea" })).Content.ReadFromJsonAsync<PlanRow>(ApiFixture.JsonOptions))!;
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"/api/v1/platform/plans/{plan.Id}/prices", new { effectiveFrom = NextMonth(), monthlyFee = fee })).StatusCode);
        var tenantId = await api.CreateTenantAsync($"{name} {Guid.NewGuid():N}"[..28]);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/plan", new { planId = plan.Id })).StatusCode);
        var owner = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        await api.Services.GetRequiredService<ICollectionProcessor>().RunAsync(At(2, 2), CancellationToken.None);
        var charge = (await admin.GetFromJsonAsync<List<ChargeRow>>($"/api/v1/platform/charges?tenantId={tenantId}", ApiFixture.JsonOptions))!.Single();
        return new Account(tenantId, owner, api.ClientFor(await api.LoginOkAsync(owner.Email, owner.Password)), charge);
    }

    /// <summary>The payment as the person would make it, at a day after the charge was issued (the clock of the tests is moved for the call).</summary>
    private async Task<Result> PayAsync(Account account, string token, string email = "dueno@cliente.test")
    {
        using var moved = api.Clock.At(At(2, 3));
        return await PayWithoutMovingAsync(account, token, email);
    }

    private async Task<Result> PayWithoutMovingAsync(Account account, string token, string email = "dueno@cliente.test")
    {
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UseTenant(new TenantId(account.TenantId));
        var payments = ActivatorUtilities.CreateInstance<ChargePayments>(scope.ServiceProvider, (ICurrentUser)new OwnerUser(account.Owner.Id, account.TenantId));
        var result = await payments.PayAsync(account.Charge.Id, new PayChargeRequest(token, email), CancellationToken.None);
        return new Result(result.IsSuccess ? result.Value : null, result.IsSuccess ? null : result.Error.Code);
    }

    private sealed record Result(PayChargeResult? Value, string? ErrorCode);

    /// <summary>The e-mail written while the clock was moved is due at that moment: it is sent with the clock moved to it again.</summary>
    private async Task DrainAtAsync()
    {
        using var moved = api.Clock.At(At(2, 3).AddMinutes(5));
        await api.DrainMailAsync();
    }

    private async Task<ChargeRow> ChargeNowAsync(Account account)
    {
        using var admin = await api.AdminClientAsync();
        return (await admin.GetFromJsonAsync<List<ChargeRow>>($"/api/v1/platform/charges?tenantId={account.TenantId}", ApiFixture.JsonOptions))!.Single();
    }

    private async Task<List<(string State, string? GatewayChargeId)>> AttemptsAsync(Guid chargeId)
    {
        await using var connection = new NpgsqlConnection(api.Postgres.OwnerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT state, gateway_charge_id FROM subscription.payment_attempt WHERE charge_id = @c ORDER BY id", connection);
        command.Parameters.AddWithValue("c", chargeId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(string, string?)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return rows;
    }

    // ---------- paying ----------

    [Fact]
    public async Task A_card_payment_that_the_bank_accepts_is_recorded_in_the_charge_with_the_reference_of_the_gateway_and_the_person_is_told()
    {
        await UseSandboxAsync();
        var account = await ChargedAsync("Pago aceptado");

        var paid = await PayAsync(account, Token("OK"));

        Assert.Equal(PayStatus.Paid, paid.Value!.Status);
        Assert.Equal((118m, PaymentMethod.Card), (paid.Value.Payment!.Amount, paid.Value.Payment.Method));
        Assert.StartsWith("chr_test_", paid.Value.Payment.Reference, StringComparison.Ordinal);
        Assert.Equal(("Paid", 0m), ((await ChargeNowAsync(account)).Status, (await ChargeNowAsync(account)).Balance));
        var attempt = Assert.Single(await AttemptsAsync(account.Charge.Id));
        Assert.Equal(("Paid", paid.Value.Payment.Reference), attempt);

        await DrainAtAsync();
        Assert.Contains(api.Mail.To(account.Owner.Email), m => m.Subject == "Recibimos su pago de S/ 118.00");

        // There is nothing left to pay, and a second try does not charge the card again.
        Assert.Equal("SF-PAY-004", (await PayAsync(account, Token("OK"))).ErrorCode);
        Assert.Single(await AttemptsAsync(account.Charge.Id));
    }

    [Fact]
    public async Task A_refused_payment_records_nothing_and_the_person_can_try_again_with_another_card()
    {
        await UseSandboxAsync();
        var account = await ChargedAsync("Pago rechazado");

        var declined = await PayAsync(account, Token("NOFONDOS"));
        Assert.Equal(PayStatus.Declined, declined.Value!.Status);
        Assert.Contains("fondos", declined.Value.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(declined.Value.Payment);
        Assert.Equal(("Pending", 118m), ((await ChargeNowAsync(account)).Status, (await ChargeNowAsync(account)).Balance));

        var retry = await PayAsync(account, Token("OK"));
        Assert.Equal(PayStatus.Paid, retry.Value!.Status);
        Assert.Equal(["Declined", "Paid"], (await AttemptsAsync(account.Charge.Id)).Select(a => a.State).ToArray());
    }

    [Fact]
    public async Task A_bank_that_asks_for_3d_secure_and_a_gateway_that_refuses_the_request_charge_nothing_and_are_told_as_such()
    {
        await UseSandboxAsync();
        var account = await ChargedAsync("Pago 3DS");

        var authentication = await PayAsync(account, Token("3DS"));
        var rejected = await PayAsync(account, Token("ERROR"));

        Assert.Equal(PayStatus.NeedsAuthentication, authentication.Value!.Status);
        Assert.Contains("3D Secure", authentication.Value.Message, StringComparison.Ordinal);
        Assert.Equal(PayStatus.Failed, rejected.Value!.Status);
        Assert.Equal(118m, (await ChargeNowAsync(account)).Balance);
        Assert.Equal(["NeedsAuthentication", "Failed"], (await AttemptsAsync(account.Charge.Id)).Select(a => a.State).ToArray());
    }

    [Fact]
    public async Task When_the_answer_of_the_gateway_is_lost_the_attempt_waits_for_a_person_and_no_second_charge_is_made_until_it_is_closed()
    {
        await UseSandboxAsync();
        var account = await ChargedAsync("Pago sin respuesta");
        using var admin = await api.AdminClientAsync();

        var lost = await PayAsync(account, Token("CAIDA"));
        Assert.Equal(PayStatus.UnderReview, lost.Value!.Status);
        Assert.Contains("no vuelva a pagar", lost.Value.Message, StringComparison.Ordinal);
        Assert.Equal(118m, (await ChargeNowAsync(account)).Balance);

        // The person cannot pay again; the platform sees the attempt and closes it after reviewing it with the gateway.
        Assert.Equal("SF-PAY-003", (await PayAsync(account, Token("OK"))).ErrorCode);
        var listed = (await admin.GetFromJsonAsync<List<JsonElement>>("/api/v1/platform/payment-attempts", ApiFixture.JsonOptions))!;
        var attempt = Assert.Single(listed, a => a.GetProperty("chargeId").GetGuid() == account.Charge.Id);
        Assert.Equal("UnderReview", attempt.GetProperty("state").GetString());
        Assert.Equal(account.Charge.Id, attempt.GetProperty("chargeId").GetGuid());

        var attemptId = attempt.GetProperty("id").GetGuid();
        Assert.Equal("SF-PAY-002", await CodeAsync(await admin.PostAsJsonAsync($"/api/v1/platform/payment-attempts/{attemptId}/resolve", new { note = "x" })));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/payment-attempts/{attemptId}/resolve", new { note = "Revisado en el panel de la pasarela: no hubo cobro" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/platform/payment-attempts/{attemptId}/resolve", new { note = "Otra vez" })).StatusCode);
        Assert.DoesNotContain((await admin.GetFromJsonAsync<List<JsonElement>>("/api/v1/platform/payment-attempts", ApiFixture.JsonOptions))!, a => a.GetProperty("chargeId").GetGuid() == account.Charge.Id);

        Assert.Equal(PayStatus.Paid, (await PayAsync(account, Token("OK"))).Value!.Status);
    }

    [Fact]
    public async Task Two_payments_at_once_for_the_same_charge_charge_the_card_once()
    {
        await UseSandboxAsync();
        var account = await ChargedAsync("Pago doble");

        Result[] both;
        using (api.Clock.At(At(2, 3)))
        {
            both = await Task.WhenAll(PayWithoutMovingAsync(account, Token("OK")), PayWithoutMovingAsync(account, Token("OK", "2")));
        }


        Assert.Equal(1, both.Count(r => r.Value?.Status == PayStatus.Paid));
        Assert.Equal(1, both.Count(r => r.ErrorCode is "SF-PAY-003" or "SF-PAY-004"));
        Assert.Single(await AttemptsAsync(account.Charge.Id));
        Assert.Equal(("Paid", 0m), ((await ChargeNowAsync(account)).Status, (await ChargeNowAsync(account)).Balance));
    }

    [Fact]
    public async Task What_is_charged_is_the_balance_not_the_total_and_an_amount_the_gateway_cannot_take_is_refused()
    {
        await UseSandboxAsync();
        var partial = await ChargedAsync("Pago de saldo");
        await using (var moment = api.Services.CreateAsyncScope())
        {
            using var moved = api.Clock.At(At(2, 3));
            moment.ServiceProvider.GetRequiredService<DataScope>().UsePlatform("payment gateway test");
            Assert.True((await moment.ServiceProvider.GetRequiredService<ICollections>().RecordPaymentAsync(partial.Charge.Id, new RecordPaymentRequest(50m, PaymentMethod.Transfer, LimaCalendar.Today(At(2, 3)), "Operación 1", null), CancellationToken.None)).IsSuccess);
        }

        var paid = await PayAsync(partial, Token("OK"));
        Assert.Equal(68m, paid.Value!.Payment!.Amount);
        Assert.Equal("Paid", (await ChargeNowAsync(partial)).Status);

        var tiny = await ChargedAsync("Pago minimo", fee: 0.50m);
        Assert.Equal("SF-PAY-006", (await PayAsync(tiny, Token("OK"))).ErrorCode); // 0.59 is under S/ 1.00
        Assert.Empty(await AttemptsAsync(tiny.Charge.Id));
    }

    [Fact]
    public async Task The_payment_is_the_tenants_own_only_and_needs_the_permission_to_manage_billing()
    {
        await UseSandboxAsync();
        var mine = await ChargedAsync("Pago propio");
        var other = await ChargedAsync("Pago ajeno");
        using var admin = await api.AdminClientAsync();
        var reader = await ApiFixture.CreateUserAsync(admin, Roles.ReadOnly, mine.TenantId);
        using var readOnly = api.ClientFor(await api.LoginOkAsync(reader.Email, reader.Password));
        var body = new { tokenId = Token("OK"), email = "dueno@cliente.test" };

        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync($"/api/v1/charges/{mine.Charge.Id}/pay", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.GetAsync("/api/v1/payment-config")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostAsJsonAsync($"/api/v1/charges/{mine.Charge.Id}/pay", body)).StatusCode); // another account's charge does not exist for it
        Assert.Equal(HttpStatusCode.NotFound, (await mine.Client.PostAsJsonAsync($"/api/v1/charges/{Guid.NewGuid()}/pay", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/v1/charges/{mine.Charge.Id}/pay", body)).StatusCode); // nor does the platform pay in the name of an account
        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/v1/charges/{mine.Charge.Id}/pay", body)).StatusCode);
        Assert.Empty(await AttemptsAsync(mine.Charge.Id));
    }

    [Fact]
    public async Task A_token_or_an_e_mail_that_is_not_one_is_refused_before_anything_is_written()
    {
        await UseSandboxAsync();
        var account = await ChargedAsync("Pago invalido");

        foreach (var body in new object[]
        {
            new { tokenId = "numero-de-tarjeta-4111111111111111", email = "dueno@cliente.test" },
            new { tokenId = "tkn_test_corto", email = "dueno@cliente.test" },
            new { tokenId = Token("OK"), email = "no-es-un-correo" },
            new { tokenId = Token("OK"), email = new string('a', 60) + "@cliente.test" },
        })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await account.Client.PostAsJsonAsync($"/api/v1/charges/{account.Charge.Id}/pay", body)).StatusCode);
        }

        Assert.Empty(await AttemptsAsync(account.Charge.Id));
    }

    [Fact]
    public async Task Without_the_gateway_on_nobody_pays_online_and_the_screen_is_told_so()
    {
        var account = await ChargedAsync("Pago apagado");
        await UseSandboxAsync(enabled: false);

        Assert.False((await account.Client.GetFromJsonAsync<ConfigRow>("/api/v1/payment-config", ApiFixture.JsonOptions))!.Enabled);
        var refused = await account.Client.PostAsJsonAsync($"/api/v1/charges/{account.Charge.Id}/pay", new { tokenId = Token("OK"), email = "dueno@cliente.test" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("SF-PAY-001", await CodeAsync(refused));

        await UseSandboxAsync();
        var config = (await account.Client.GetFromJsonAsync<ConfigRow>("/api/v1/payment-config", ApiFixture.JsonOptions))!;
        Assert.Equal((true, "Sandbox", "sandbox"), (config.Enabled, config.Provider, config.Environment));
        Assert.Null(config.PublicKey);
    }

    // ---------- the settings of the gateway ----------

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task The_keys_of_culqi_are_checked_stored_encrypted_and_never_returned_and_only_the_super_admin_sets_them()
    {
        using var admin = await api.AdminClientAsync();
        var support = await ApiFixture.CreateUserAsync(admin, Roles.PlatformSupport, null);
        using var supportClient = api.ClientFor(await api.LoginOkAsync(support.Email, support.Password));
        var secret = "sk" + "_test_AbCdEf0123456789";
        var publicKey = "pk_test_AbCdEf0123456789";

        foreach (var body in new object[]
        {
            new { provider = "Culqi", publicKey = "no-es-una-llave", secretKey = secret, enabled = true },
            new { provider = "Culqi", publicKey, secretKey = "sk_test_corta", enabled = true },
            new { provider = "Culqi", publicKey, secretKey = "sk" + "_live_AbCdEf0123456789", enabled = true }, // test and live together
            new { provider = "Culqi", publicKey = "pk_live_AbCdEf0123456789", secretKey = (string?)null, enabled = true }, // the stored secret is of another environment
            new { provider = 9, publicKey, secretKey = secret, enabled = true },
        })
        {
            var refused = await admin.PutAsJsonAsync("/api/v1/platform/payment-gateway", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("SF-PAY-002", await CodeAsync(refused));
        }

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/platform/payment-gateway", new { provider = "Culqi", publicKey, secretKey = secret, enabled = true })).StatusCode);
        var stored = await admin.GetStringAsync("/api/v1/platform/payment-gateway");
        Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
        var row = JsonSerializer.Deserialize<SettingsRow>(stored, ApiFixture.JsonOptions)!;
        Assert.Equal(("Culqi", publicKey, true, "test", true), (row.Provider, row.PublicKey, row.HasSecretKey, row.Environment, row.Enabled));

        // Changing only the public key keeps the secret that was stored; the owner of the database cannot read it in clear.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/platform/payment-gateway", new { provider = "Culqi", publicKey = "pk_test_ZyXwVu9876543210", secretKey = (string?)null, enabled = true })).StatusCode);
        await using (var connection = new NpgsqlConnection(api.Postgres.OwnerConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT protected_secret_key FROM subscription.gateway_settings", connection);
            var blob = (byte[])(await command.ExecuteScalarAsync())!;
            Assert.DoesNotContain(secret, System.Text.Encoding.UTF8.GetString(blob), StringComparison.Ordinal);
            Assert.True(blob.Length > secret.Length);
        }

        // The audit says that it changed and with which public key, never the secret.
        var events = await admin.GetStringAsync($"/api/v1/audit?action={AuditActions.PaymentGatewayConfigured}&take=10");
        Assert.DoesNotContain(secret, events, StringComparison.Ordinal);
        Assert.Contains("pk_test_ZyXwVu9876543210", events, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await supportClient.GetAsync("/api/v1/platform/payment-gateway")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await supportClient.PutAsJsonAsync("/api/v1/platform/payment-gateway", new { provider = "Sandbox", enabled = true })).StatusCode);
        var enableWithoutSecret = await admin.PutAsJsonAsync("/api/v1/platform/payment-gateway", new { provider = "Sandbox", enabled = false });
        Assert.Equal(HttpStatusCode.OK, enableWithoutSecret.StatusCode);
        Assert.Equal("SF-PAY-002", await CodeAsync(await admin.PutAsJsonAsync("/api/v1/platform/payment-gateway", new { provider = "Culqi", publicKey, secretKey = (string?)null, enabled = true })));
    }
}
