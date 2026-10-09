using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SecureFact.Identity.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Subscriptions.Application;
using SecureFact.Subscriptions.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>Prices, billing policy, charges and payments of the platform (ADR-062) and the suspension for non-payment (ADR-064).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class SubscriptionsApiTests(ApiFixture api)
{
    private sealed record PlanRow(Guid Id, string Code, string Name, int? MaxCompanies, int? MaxUsers, int? MaxDocumentsPerMonth, bool IsActive, bool AllowsOverage);

    private sealed record PriceRow(Guid Id, Guid PlanId, int Version, DateOnly EffectiveFrom, decimal MonthlyFee, int? IncludedDocuments, decimal? OverageUnitPrice, string? Note);

    private sealed record PolicyRow(Guid Id, int Version, DateOnly EffectiveFrom, int DueDays, int? SuspendAfterDays, int ReminderDays, string? Note);

    private sealed record TermsRow(Guid TenantId, Guid PlanId, string PlanCode, bool AllowsOverage, PriceRow? Price, DateOnly? FirstChargePeriod);

    private sealed record ChargeRow(
        Guid Id, Guid TenantId, string TenantName, DateOnly Period, string PlanCode, decimal MonthlyFee, int DocumentsIssued, int OverageDocuments, decimal NetAmount, decimal TaxRate, decimal TaxAmount,
        decimal TotalAmount, DateOnly IssuedOn, DateOnly DueOn, DateOnly? SuspendOn, decimal PaidAmount, decimal Balance, string Status, string? VoidReason);

    private sealed record TenantRow(Guid Id, string Status, string? SuspendedBy);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class PlatformStaff : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId => null;

        public Guid? SessionId => null;

        public TenantId? TenantId => null;

        public bool IsPlatform => true;

        public Guid? ResellerId => null;

        public IReadOnlySet<string> Roles { get; } = new HashSet<string>();

        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();

        public bool HasPermission(string permission) => true;
    }

    /// <summary>The services as they would run at a given instant, in platform scope: the payments and settlements of a month that has not come yet.</summary>
    private sealed class Moment(AsyncServiceScope scope, Collections collections, Commissions commissions) : IAsyncDisposable
    {
        public Collections Collections => collections;

        public Commissions Commissions => commissions;

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString()!;
    }

    private static DateOnly TodayInLima() => LimaCalendar.Today(DateTimeOffset.UtcNow);

    private static DateOnly NextMonth(int months = 1) => LimaCalendar.MonthStart(TodayInLima()).AddMonths(months);

    /// <summary>Noon, Lima time, on a day of the month that is <paramref name="monthsAhead"/> months after the current one.</summary>
    private static DateTimeOffset At(int monthsAhead, int day) => LimaCalendar.StartOf(NextMonth(monthsAhead).AddDays(day - 1)).AddHours(12);

    private async Task<Moment> AtAsync(DateTimeOffset when)
    {
        var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UsePlatform("subscriptions test");
        var clock = new FixedClock(when);
        var commissions = ActivatorUtilities.CreateInstance<Commissions>(scope.ServiceProvider, (TimeProvider)clock, (ICurrentUser)new PlatformStaff());
        var collections = ActivatorUtilities.CreateInstance<Collections>(scope.ServiceProvider, (TimeProvider)clock, commissions);
        return new Moment(scope, collections, commissions);
    }

    private Task<CollectionPassResult> RunAsync(DateTimeOffset when) =>
        api.Services.GetRequiredService<ICollectionProcessor>().RunAsync(when, CancellationToken.None);

    private static async Task<PlanRow> NewPlanAsync(HttpClient admin, bool overage = false, int? documents = null)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/platform/plans", new
        {
            code = $"sub-{Guid.NewGuid():N}"[..16],
            name = "Plan con precio",
            maxDocumentsPerMonth = documents,
            allowsOverage = overage,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PlanRow>(ApiFixture.JsonOptions))!;
    }

    private static async Task<HttpResponseMessage> PublishAsync(HttpClient admin, PlanRow plan, decimal fee, DateOnly? from = null, int? included = null, decimal? unit = null) =>
        await admin.PostAsJsonAsync($"/api/v1/platform/plans/{plan.Id}/prices", new { effectiveFrom = from ?? NextMonth(), monthlyFee = fee, includedDocuments = included, overageUnitPrice = unit, note = "Precio de prueba" });

    private async Task<(Guid TenantId, HttpClient Owner, string Email)> NewTenantAsync(string name, HttpClient admin, PlanRow plan)
    {
        var tenantId = await api.CreateTenantAsync(name);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/plan", new { planId = plan.Id })).StatusCode);
        var user = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        return (tenantId, api.ClientFor(await api.LoginOkAsync(user.Email, user.Password)), user.Email);
    }

    private static async Task<List<ChargeRow>> ChargesOfAsync(HttpClient admin, Guid tenantId) =>
        (await admin.GetFromJsonAsync<List<ChargeRow>>($"/api/v1/platform/charges?tenantId={tenantId}", ApiFixture.JsonOptions))!;

    private static async Task<TenantRow> TenantAsync(HttpClient admin, Guid tenantId) =>
        (await admin.GetFromJsonAsync<TenantRow>($"/api/v1/platform/tenants/{tenantId}", ApiFixture.JsonOptions))!;

    /// <summary>A tenant on a plan with a monthly fee of 100, and its charge for the first month, issued on the second day of the month after next.</summary>
    private async Task<(Guid TenantId, HttpClient Owner, string Email, ChargeRow Charge)> ChargedTenantAsync(HttpClient admin, string name, decimal fee = 100m)
    {
        var plan = await NewPlanAsync(admin);
        Assert.Equal(HttpStatusCode.Created, (await PublishAsync(admin, plan, fee)).StatusCode);
        var (tenantId, owner, email) = await NewTenantAsync(name, admin, plan);
        await RunAsync(At(2, 2));
        var charge = Assert.Single(await ChargesOfAsync(admin, tenantId));
        return (tenantId, owner, email, charge);
    }

    [Fact]
    public async Task Prices_are_published_by_the_super_admin_and_read_by_support_and_nobody_else()
    {
        using var admin = await api.AdminClientAsync();
        var plan = await NewPlanAsync(admin);
        var support = await ApiFixture.CreateUserAsync(admin, Roles.PlatformSupport, null);
        using var supportClient = api.ClientFor(await api.LoginOkAsync(support.Email, support.Password));
        var (_, owner, _) = await NewTenantAsync("Sin acceso a precios SAC", admin, plan);

        Assert.Equal(HttpStatusCode.Created, (await PublishAsync(admin, plan, 99.90m)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PublishAsync(supportClient, plan, 50m, NextMonth(2))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PublishAsync(owner, plan, 50m, NextMonth(2))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await supportClient.GetAsync($"/api/v1/platform/plans/{plan.Id}/prices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v1/platform/plans/{plan.Id}/prices")).StatusCode);
        using var anonymous = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/platform/plans/{plan.Id}/prices")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/v1/platform/plans/{Guid.NewGuid()}/prices", new { effectiveFrom = NextMonth(), monthlyFee = 10m })).StatusCode);
    }

    [Fact]
    public async Task A_price_starts_on_the_first_of_a_future_month_after_the_last_version_and_matches_what_the_plan_charges()
    {
        using var admin = await api.AdminClientAsync();
        var plan = await NewPlanAsync(admin);

        var thisMonth = LimaCalendar.MonthStart(TodayInLima());
        foreach (var from in new[] { NextMonth().AddDays(1), thisMonth, thisMonth.AddMonths(-1) })
        {
            var refused = await PublishAsync(admin, plan, 100m, from);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("SF-SUB-001", await CodeAsync(refused));
        }

        Assert.Equal("SF-SUB-001", await CodeAsync(await PublishAsync(admin, plan, -1m)));
        Assert.Equal("SF-SUB-001", await CodeAsync(await PublishAsync(admin, plan, 10.005m)));
        Assert.Equal("SF-SUB-001", await CodeAsync(await PublishAsync(admin, plan, 10m, included: 5, unit: 0.5m))); // this plan does not charge the overage

        var first = (await (await PublishAsync(admin, plan, 100m, NextMonth(2))).Content.ReadFromJsonAsync<PriceRow>(ApiFixture.JsonOptions))!;
        Assert.Equal((1, 100m, null, null), (first.Version, first.MonthlyFee, first.IncludedDocuments, first.OverageUnitPrice));
        // The next version has to come after the last one: the history is never rewritten.
        Assert.Equal("SF-SUB-001", await CodeAsync(await PublishAsync(admin, plan, 120m, NextMonth(2))));
        Assert.Equal("SF-SUB-001", await CodeAsync(await PublishAsync(admin, plan, 120m, NextMonth())));
        var second = (await (await PublishAsync(admin, plan, 120m, NextMonth(3))).Content.ReadFromJsonAsync<PriceRow>(ApiFixture.JsonOptions))!;
        Assert.Equal(2, second.Version);

        var versions = (await admin.GetFromJsonAsync<List<PriceRow>>($"/api/v1/platform/plans/{plan.Id}/prices", ApiFixture.JsonOptions))!;
        Assert.Equal([100m, 120m], versions.Select(v => v.MonthlyFee));

        // A plan that charges the overage says how many documents the fee includes and what each extra one costs.
        var overage = await NewPlanAsync(admin, overage: true);
        Assert.Equal("SF-SUB-001", await CodeAsync(await PublishAsync(admin, overage, 100m)));
        Assert.Equal("SF-SUB-001", await CodeAsync(await PublishAsync(admin, overage, 100m, included: 10)));
        Assert.Equal("SF-SUB-001", await CodeAsync(await PublishAsync(admin, overage, 100m, included: 10, unit: 0m)));
        Assert.Equal("SF-SUB-001", await CodeAsync(await PublishAsync(admin, overage, 100m, included: 10, unit: 0.12345m)));
        var priced = (await (await PublishAsync(admin, overage, 100m, included: 10, unit: 0.2m)).Content.ReadFromJsonAsync<PriceRow>(ApiFixture.JsonOptions))!;
        Assert.Equal((10, 0.2m), (priced.IncludedDocuments, priced.OverageUnitPrice));
    }

    [Fact]
    public async Task Whether_a_plan_charges_the_overage_is_fixed_when_it_is_created()
    {
        using var admin = await api.AdminClientAsync();
        var plan = await NewPlanAsync(admin, overage: true, documents: 50);
        Assert.True(plan.AllowsOverage);

        var flip = await admin.PutAsJsonAsync($"/api/v1/platform/plans/{plan.Id}", new { code = plan.Code, name = plan.Name, allowsOverage = false });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, flip.StatusCode);
        Assert.Equal("SF-PLAN-003", await CodeAsync(flip));
        // Saying nothing, or the same, changes nothing.
        Assert.True((await (await admin.PutAsJsonAsync($"/api/v1/platform/plans/{plan.Id}", new { code = plan.Code, name = "Renombrado", maxDocumentsPerMonth = 60 })).Content.ReadFromJsonAsync<PlanRow>(ApiFixture.JsonOptions))!.AllowsOverage);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/platform/plans/{plan.Id}", new { code = plan.Code, name = "Renombrado", allowsOverage = true })).StatusCode);
    }

    [Fact]
    public async Task Documents_over_the_allowance_of_a_plan_that_charges_the_overage_are_never_refused()
    {
        using var admin = await api.AdminClientAsync();
        var plan = await NewPlanAsync(admin, overage: true, documents: 1);
        var (_, owner, _) = await NewTenantAsync("Con excedente SAC", admin, plan);
        var company = (await (await owner.PostAsJsonAsync("/api/v1/companies", new { ruc = "20100066603", details = new { legalName = "Emisora SAC", fiscalAddress = "Av. Larco 123", ubigeo = "150122" } }))
            .Content.ReadFromJsonAsync<JsonElement>(ApiFixture.JsonOptions));
        var series = (await (await owner.PostAsJsonAsync("/api/v1/series", new { companyId = company.GetProperty("id").GetGuid(), documentTypeCode = "01", code = "F001" })).Content.ReadFromJsonAsync<JsonElement>(ApiFixture.JsonOptions));

        async Task<HttpStatusCode> IssueAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents")
            {
                Content = JsonContent.Create(new
                {
                    seriesId = series.GetProperty("id").GetGuid(),
                    issueDate = DateTimeOffset.UtcNow.AddHours(-5).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    currency = "PEN",
                    buyer = new { documentTypeCode = "6", documentNumber = "20100066603", name = "Cliente SAC" },
                    lines = new[] { new { description = "Servicio", unitCode = "ZZ", tax = new { quantity = 1m, unitValue = 100m, igvAffectationCode = "10" } } },
                }),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            return (await owner.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Created, await IssueAsync());
        Assert.Equal(HttpStatusCode.Created, await IssueAsync());
        Assert.Equal(HttpStatusCode.Created, await IssueAsync());
    }

    [Fact]
    public async Task The_billing_policy_is_versioned_in_the_future_and_the_first_version_exists()
    {
        using var admin = await api.AdminClientAsync();
        var policies = (await admin.GetFromJsonAsync<List<PolicyRow>>("/api/v1/platform/billing-policies", ApiFixture.JsonOptions))!;
        Assert.Equal((1, 10, 15, 3), (policies[0].Version, policies[0].DueDays, policies[0].SuspendAfterDays, policies[0].ReminderDays));

        var today = TodayInLima();
        foreach (var body in new object[]
        {
            new { effectiveFrom = today, dueDays = 5, suspendAfterDays = 5 },
            new { effectiveFrom = today.AddDays(30), dueDays = -1, suspendAfterDays = 5 },
            new { effectiveFrom = today.AddDays(30), dueDays = 91, suspendAfterDays = 5 },
            new { effectiveFrom = today.AddDays(30), dueDays = 5, suspendAfterDays = 366 },
            new { effectiveFrom = today.AddDays(30), dueDays = 5, suspendAfterDays = 5, reminderDays = -1 },
            new { effectiveFrom = today.AddDays(30), dueDays = 5, suspendAfterDays = 5, reminderDays = 31 },
            new { effectiveFrom = today.AddDays(30), dueDays = 5, suspendAfterDays = 5, note = new string('x', 301) },
        })
        {
            var refused = await admin.PostAsJsonAsync("/api/v1/platform/billing-policies", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("SF-SUB-002", await CodeAsync(refused));
        }

        // A version far in the future, so it changes nothing for the charges of the other tests; a later one has to come after it.
        var date = today.AddYears(30);
        var created = await admin.PostAsJsonAsync("/api/v1/platform/billing-policies", new { effectiveFrom = date, dueDays = 7, suspendAfterDays = (int?)null, reminderDays = 0, note = "Sin suspensión" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(0, (await created.Content.ReadFromJsonAsync<PolicyRow>(ApiFixture.JsonOptions))!.ReminderDays);
        Assert.Equal("SF-SUB-002", await CodeAsync(await admin.PostAsJsonAsync("/api/v1/platform/billing-policies", new { effectiveFrom = date, dueDays = 7, suspendAfterDays = 1 })));
        Assert.Equal("SF-SUB-002", await CodeAsync(await admin.PostAsJsonAsync("/api/v1/platform/billing-policies", new { effectiveFrom = today.AddDays(40), dueDays = 7, suspendAfterDays = 1 })));
    }

    [Fact]
    public async Task A_tenant_keeps_the_price_it_had_when_it_took_the_plan_and_a_later_version_does_not_reach_it()
    {
        using var admin = await api.AdminClientAsync();
        var plan = await NewPlanAsync(admin);
        await PublishAsync(admin, plan, 100m, NextMonth());
        var (tenantId, owner, _) = await NewTenantAsync("Precio fijo SAC", admin, plan);
        await PublishAsync(admin, plan, 150m, NextMonth(3));

        var terms = (await admin.GetFromJsonAsync<TermsRow>($"/api/v1/platform/tenants/{tenantId}/terms", ApiFixture.JsonOptions))!;
        Assert.Equal((1, 100m), (terms.Price!.Version, terms.Price.MonthlyFee));
        Assert.Equal(NextMonth(), terms.FirstChargePeriod);
        var own = (await owner.GetFromJsonAsync<TermsRow>("/api/v1/subscription", ApiFixture.JsonOptions))!;
        Assert.Equal(100m, own.Price!.MonthlyFee);

        // At the end of the second month the tenant is charged with the first version, not with the one that came after.
        await RunAsync(At(3, 2));
        var charges = (await ChargesOfAsync(admin, tenantId)).OrderBy(c => c.Period).ToList();
        Assert.Equal([NextMonth(), NextMonth(2)], charges.Select(c => c.Period));
        Assert.All(charges, c => Assert.Equal(100m, c.MonthlyFee));

        // A tenant on a plan without a price owes nothing and has no first month to be charged.
        var pilot = await api.CreateTenantAsync("Sin precio SAC");
        var free = (await admin.GetFromJsonAsync<TermsRow>($"/api/v1/platform/tenants/{pilot}/terms", ApiFixture.JsonOptions))!;
        Assert.Null(free.Price);
        Assert.Null(free.FirstChargePeriod);
        Assert.Empty(await ChargesOfAsync(admin, pilot));
        // Reading the terms of another tenant is for platform staff.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v1/platform/tenants/{pilot}/terms")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/platform/tenants/{Guid.NewGuid()}/terms")).StatusCode);
    }

    [Fact]
    public async Task A_month_that_closed_is_charged_once_with_the_tax_and_the_dates_of_the_policy_and_only_its_tenant_sees_it()
    {
        using var admin = await api.AdminClientAsync();
        var (tenantId, owner, _, charge) = await ChargedTenantAsync(admin, "Cobrada SAC");
        var (otherId, other, _, otherCharge) = await ChargedTenantAsync(admin, "Ajena SAC", 40m);

        Assert.Equal((NextMonth(), 100m, 0.18m, 18m, 118m), (charge.Period, charge.NetAmount, charge.TaxRate, charge.TaxAmount, charge.TotalAmount));
        var issued = LimaCalendar.Today(At(2, 2));
        Assert.Equal((issued, issued.AddDays(10), issued.AddDays(25)), (charge.IssuedOn, charge.DueOn, charge.SuspendOn));
        Assert.Equal(("Pending", 0m, 118m, 0), (charge.Status, charge.PaidAmount, charge.Balance, charge.OverageDocuments));

        // Another pass does not charge the same month again.
        await RunAsync(At(2, 3));
        await RunAsync(At(2, 20));
        Assert.Single(await ChargesOfAsync(admin, tenantId));

        var mine = (await owner.GetFromJsonAsync<List<ChargeRow>>("/api/v1/charges", ApiFixture.JsonOptions))!;
        Assert.Equal(charge.Id, Assert.Single(mine).Id);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/v1/charges/{charge.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/v1/charges/{otherCharge.Id}")).StatusCode);
        Assert.Equal(otherCharge.Id, Assert.Single((await other.GetFromJsonAsync<List<ChargeRow>>("/api/v1/charges", ApiFixture.JsonOptions))!).Id);
        // A tenant cannot ask for another tenant's charges with a filter, nor use the platform routes.
        Assert.Equal(charge.Id, Assert.Single((await owner.GetFromJsonAsync<List<ChargeRow>>($"/api/v1/charges?tenantId={otherId}", ApiFixture.JsonOptions))!).Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/v1/platform/charges")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/charges")).StatusCode);

        var byStatus = (await admin.GetFromJsonAsync<List<ChargeRow>>($"/api/v1/platform/charges?tenantId={tenantId}&status=Pending&period={NextMonth():yyyy-MM}", ApiFixture.JsonOptions))!;
        Assert.Single(byStatus);
        Assert.Empty((await admin.GetFromJsonAsync<List<ChargeRow>>($"/api/v1/platform/charges?tenantId={tenantId}&status=Paid", ApiFixture.JsonOptions))!);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.GetAsync("/api/v1/platform/charges?period=octubre")).StatusCode);
    }

    [Fact]
    public async Task Payments_settle_a_charge_a_mistake_is_cancelled_with_a_reversal_and_nothing_is_edited()
    {
        using var admin = await api.AdminClientAsync();
        var (tenantId, _, _, charge) = await ChargedTenantAsync(admin, "Pagadora SAC");
        var paidOn = LimaCalendar.Today(At(2, 3));
        await using var moment = await AtAsync(At(2, 3));
        RecordPaymentRequest Pay(decimal amount) => new(amount, PaymentMethod.Transfer, paidOn, "Operación 4521", null);

        foreach (var bad in new[] { 0m, -5m, 10.005m, 118.01m })
        {
            var refused = await moment.Collections.RecordPaymentAsync(charge.Id, Pay(bad), CancellationToken.None);
            Assert.False(refused.IsSuccess);
            Assert.Equal("SF-SUB-004", refused.Error.Code);
        }

        Assert.Equal("SF-SUB-004", (await moment.Collections.RecordPaymentAsync(charge.Id, Pay(10m) with { PaidOn = paidOn.AddDays(1) }, CancellationToken.None)).Error.Code); // future
        Assert.Equal("SF-SUB-004", (await moment.Collections.RecordPaymentAsync(charge.Id, Pay(10m) with { PaidOn = paidOn.AddDays(-30) }, CancellationToken.None)).Error.Code); // before the charge existed
        Assert.Equal("SF-SUB-003", (await moment.Collections.RecordPaymentAsync(Guid.NewGuid(), Pay(10m), CancellationToken.None)).Error.Code);

        var partial = (await moment.Collections.RecordPaymentAsync(charge.Id, Pay(18m), CancellationToken.None)).Value;
        var detail = (await moment.Collections.GetChargeAsync(charge.Id, CancellationToken.None)).Value;
        Assert.Equal((ChargeStatus.Partial, 18m, 100m), (detail.Charge.Status, detail.Charge.PaidAmount, detail.Charge.Balance));

        Assert.Equal("SF-SUB-004", (await moment.Collections.RecordPaymentAsync(charge.Id, Pay(100.01m), CancellationToken.None)).Error.Code);
        var rest = (await moment.Collections.RecordPaymentAsync(charge.Id, Pay(100m), CancellationToken.None)).Value;
        Assert.Equal(ChargeStatus.Paid, (await moment.Collections.GetChargeAsync(charge.Id, CancellationToken.None)).Value.Charge.Status);
        Assert.Single((await admin.GetFromJsonAsync<List<ChargeRow>>($"/api/v1/platform/charges?tenantId={tenantId}&status=Paid", ApiFixture.JsonOptions))!);

        // The second payment was a mistake: a reversal of the opposite amount brings the balance back, and it cannot be reversed again, nor can a reversal be.
        var reversal = (await moment.Collections.ReversePaymentAsync(rest.Id, "Se registró dos veces", CancellationToken.None)).Value;
        Assert.Equal((-100m, rest.Id), (reversal.Amount, reversal.ReversesPaymentId));
        var after = (await moment.Collections.GetChargeAsync(charge.Id, CancellationToken.None)).Value;
        Assert.Equal((ChargeStatus.Partial, 100m, 3), (after.Charge.Status, after.Charge.Balance, after.Payments.Count));
        Assert.Equal(SecureFact.SharedKernel.Results.ErrorKind.Conflict, (await moment.Collections.ReversePaymentAsync(rest.Id, "Otra vez", CancellationToken.None)).Error.Kind);
        Assert.Equal("SF-SUB-004", (await moment.Collections.ReversePaymentAsync(reversal.Id, "Revertir la reversa", CancellationToken.None)).Error.Code);
        Assert.Equal("SF-SUB-004", (await moment.Collections.ReversePaymentAsync(partial.Id, "ab", CancellationToken.None)).Error.Code); // the reason
        Assert.Equal("SF-SUB-005", (await moment.Collections.ReversePaymentAsync(Guid.NewGuid(), "No existe", CancellationToken.None)).Error.Code);

        // A charge with net payments cannot be voided; once they are reversed it can, and then it takes no more payments.
        Assert.Equal("SF-SUB-004", (await moment.Collections.VoidChargeAsync(charge.Id, "Cargo duplicado", CancellationToken.None)).Error.Code);
        Assert.True((await moment.Collections.ReversePaymentAsync(partial.Id, "Pago de otra cuenta", CancellationToken.None)).IsSuccess);
        var voided = (await moment.Collections.VoidChargeAsync(charge.Id, "Cargo duplicado", CancellationToken.None)).Value;
        Assert.Equal((ChargeStatus.Void, 0m, "Cargo duplicado"), (voided.Status, voided.Balance, voided.VoidReason));
        Assert.Equal("SF-SUB-004", (await moment.Collections.RecordPaymentAsync(charge.Id, Pay(10m), CancellationToken.None)).Error.Code);
        Assert.Equal("SF-SUB-004", (await moment.Collections.VoidChargeAsync(charge.Id, "Otra vez", CancellationToken.None)).Error.Code);
    }

    [Fact]
    public async Task Recording_payments_is_for_the_super_admin_and_the_actions_are_audited()
    {
        using var admin = await api.AdminClientAsync();
        var (tenantId, owner, _, charge) = await ChargedTenantAsync(admin, "Auditada SAC");
        var support = await ApiFixture.CreateUserAsync(admin, Roles.PlatformSupport, null);
        using var supportClient = api.ClientFor(await api.LoginOkAsync(support.Email, support.Password));
        var body = new { amount = 10m, method = "Transfer", paidOn = TodayInLima() };

        Assert.Equal(HttpStatusCode.Forbidden, (await supportClient.PostAsJsonAsync($"/api/v1/platform/charges/{charge.Id}/payments", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/v1/platform/charges/{charge.Id}/payments", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await supportClient.PostAsJsonAsync($"/api/v1/platform/charges/{charge.Id}/void", new { reason = "Prueba" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/v1/platform/payments/{Guid.NewGuid()}/reverse", new { reason = "Prueba" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await supportClient.PostAsync("/api/v1/platform/subscriptions/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await supportClient.GetAsync($"/api/v1/platform/charges/{charge.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/v1/platform/charges/{Guid.NewGuid()}/payments", body)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync($"/api/v1/platform/charges/{charge.Id}/payments", body)).StatusCode); // the charge is not issued yet in real time: it is from the future

        await using var moment = await AtAsync(At(2, 3));
        await moment.Collections.RecordPaymentAsync(charge.Id, new RecordPaymentRequest(18m, PaymentMethod.Cash, LimaCalendar.Today(At(2, 3)), null, null), CancellationToken.None);

        var events = (await admin.GetFromJsonAsync<List<JsonElement>>($"/api/v1/audit?tenantId={tenantId}&take=50", ApiFixture.JsonOptions))!;
        Assert.Contains(events, e => e.GetProperty("action").GetString() == "subscriptions.payment.recorded");
    }

    [Fact]
    public async Task An_account_with_a_charge_unpaid_past_its_grace_is_suspended_and_paying_it_lifts_the_suspension()
    {
        using var admin = await api.AdminClientAsync();
        var (tenantId, owner, email, charge) = await ChargedTenantAsync(admin, "Morosa SAC");
        var (paidId, _, _, paidCharge) = await ChargedTenantAsync(admin, "Puntual SAC");
        var late = At(2, 2).AddDays(26); // 10 days to pay and 15 of grace: the suspension is on day 25
        await using var moment = await AtAsync(At(2, 3));
        Assert.True((await moment.Collections.RecordPaymentAsync(paidCharge.Id, new RecordPaymentRequest(118m, PaymentMethod.Deposit, LimaCalendar.Today(At(2, 3)), "Depósito 77", null), CancellationToken.None)).IsSuccess);

        // Before the grace ends, nothing happens; the due date itself only makes the charge overdue.
        await RunAsync(At(2, 2).AddDays(20));
        Assert.Equal("Active", (await TenantAsync(admin, tenantId)).Status);

        var pass = await RunAsync(late);
        Assert.True(pass.TenantsSuspended >= 1);
        var suspended = await TenantAsync(admin, tenantId);
        Assert.Equal(("Suspended", "NonPayment"), (suspended.Status, suspended.SuspendedBy));
        Assert.Equal("Active", (await TenantAsync(admin, paidId)).Status);
        var login = await api.LoginAsync(email, ApiFixture.StrongPassword);
        Assert.Equal("SF-TEN-002", await CodeAsync(login));
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/v1/companies")).StatusCode);

        // A second pass does not suspend again; a partial payment does not lift it; the rest does.
        Assert.Equal(0, (await RunAsync(late.AddHours(1))).TenantsSuspended);
        await using var later = await AtAsync(late);
        var today = LimaCalendar.Today(late);
        Assert.True((await later.Collections.RecordPaymentAsync(charge.Id, new RecordPaymentRequest(50m, PaymentMethod.Transfer, today, null, null), CancellationToken.None)).IsSuccess);
        Assert.Equal("Suspended", (await TenantAsync(admin, tenantId)).Status);
        Assert.True((await later.Collections.RecordPaymentAsync(charge.Id, new RecordPaymentRequest(68m, PaymentMethod.Transfer, today, null, null), CancellationToken.None)).IsSuccess);
        Assert.Equal("Active", (await TenantAsync(admin, tenantId)).Status);
        using var back = api.ClientFor(await api.LoginOkAsync(email, ApiFixture.StrongPassword));
        Assert.Equal(HttpStatusCode.OK, (await back.GetAsync("/api/v1/companies")).StatusCode);
    }

    [Fact]
    public async Task A_payment_never_lifts_a_suspension_that_was_not_for_non_payment_and_voiding_a_charge_does()
    {
        using var admin = await api.AdminClientAsync();
        var (platformId, _, _, platformCharge) = await ChargedTenantAsync(admin, "Suspendida por la plataforma SAC");
        var (voidId, _, _, voidedCharge) = await ChargedTenantAsync(admin, "Cargo anulado SAC");
        var late = At(2, 2).AddDays(26);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{platformId}/status", new { status = "Suspended", reason = "Uso indebido" })).StatusCode);
        await RunAsync(late);
        Assert.Equal("Suspended", (await TenantAsync(admin, voidId)).Status);
        await using var moment = await AtAsync(late);
        Assert.True((await moment.Collections.RecordPaymentAsync(platformCharge.Id, new RecordPaymentRequest(118m, PaymentMethod.Cash, LimaCalendar.Today(late), null, null), CancellationToken.None)).IsSuccess);
        var kept = await TenantAsync(admin, platformId);
        Assert.Equal(("Suspended", "Platform"), (kept.Status, kept.SuspendedBy));

        // The charge was a mistake: voiding it leaves nothing unpaid past its grace and the account is back.
        Assert.True((await moment.Collections.VoidChargeAsync(voidedCharge.Id, "Cargo generado por error", CancellationToken.None)).IsSuccess);
        Assert.Equal("Active", (await TenantAsync(admin, voidId)).Status);
    }

    [Fact]
    public async Task What_was_published_paid_or_charged_cannot_be_edited_or_deleted_even_by_the_owner_of_the_schema()
    {
        using var admin = await api.AdminClientAsync();
        var (_, _, _, charge) = await ChargedTenantAsync(admin, "Inmutable SAC");
        await using var moment = await AtAsync(At(2, 3));
        var payment = (await moment.Collections.RecordPaymentAsync(charge.Id, new RecordPaymentRequest(18m, PaymentMethod.Cash, LimaCalendar.Today(At(2, 3)), null, null), CancellationToken.None)).Value;
        var plan = await NewPlanAsync(admin);
        await PublishAsync(admin, plan, 10m);

        await using var connection = new NpgsqlConnection(api.Postgres.OwnerConnectionString);
        await connection.OpenAsync();

        async Task<string> ExecuteAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            return error.SqlState;
        }

        Assert.Equal("42501", await ExecuteAsync($"UPDATE subscription.payment SET amount = 1 WHERE id = '{payment.Id}'"));
        Assert.Equal("42501", await ExecuteAsync($"DELETE FROM subscription.payment WHERE id = '{payment.Id}'"));
        Assert.Equal("42501", await ExecuteAsync($"UPDATE subscription.charge SET total_amount = 1, net_amount = 1, tax_amount = 0 WHERE id = '{charge.Id}'"));
        Assert.Equal("42501", await ExecuteAsync($"UPDATE subscription.charge SET due_on = due_on + 30 WHERE id = '{charge.Id}'"));
        Assert.Equal("42501", await ExecuteAsync($"DELETE FROM subscription.charge WHERE id = '{charge.Id}'"));
        Assert.Equal("42501", await ExecuteAsync($"UPDATE subscription.plan_price SET monthly_fee = 1 WHERE plan_id = '{plan.Id}'"));
        Assert.Equal("42501", await ExecuteAsync($"DELETE FROM subscription.plan_price WHERE plan_id = '{plan.Id}'"));
        Assert.Equal("42501", await ExecuteAsync("UPDATE subscription.billing_policy SET due_days = 1"));
        Assert.Equal("23514", await ExecuteAsync($"INSERT INTO subscription.plan_price (id, plan_id, version, effective_from, monthly_fee, created_at) VALUES (gen_random_uuid(), '{plan.Id}', 9, DATE '2090-01-15', 1, now())"));
    }

    [Fact]
    public async Task The_row_level_security_hides_the_charges_of_other_tenants_from_the_runtime_role()
    {
        using var admin = await api.AdminClientAsync();
        var (tenantId, _, _, charge) = await ChargedTenantAsync(admin, "Visible SAC");
        var (otherId, _, _, _) = await ChargedTenantAsync(admin, "Oculta SAC");

        await using var connection = new NpgsqlConnection(api.Postgres.AppConnectionString);
        await connection.OpenAsync();

        async Task<long> CountAsync(string tenantSetting, string sql)
        {
            await using var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{tenantSetting}', false), set_config('app.scope', '', false)", connection);
            await set.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        Assert.Equal(1, await CountAsync(tenantId.ToString(), "SELECT count(*) FROM subscription.charge"));
        Assert.Equal(0, await CountAsync(tenantId.ToString(), $"SELECT count(*) FROM subscription.charge WHERE tenant_id = '{otherId}'"));
        Assert.Equal(1, await CountAsync(otherId.ToString(), $"SELECT count(*) FROM subscription.charge WHERE tenant_id = '{otherId}'"));
        Assert.Equal(0, await CountAsync(otherId.ToString(), $"SELECT count(*) FROM subscription.charge WHERE id = '{charge.Id}'"));
        // The commissions and their terms are not for any tenant at all.
        Assert.Equal(0, await CountAsync(tenantId.ToString(), "SELECT count(*) FROM subscription.commission_schedule"));
        Assert.Equal(0, await CountAsync(tenantId.ToString(), "SELECT count(*) FROM subscription.commission_entry"));
    }
}
