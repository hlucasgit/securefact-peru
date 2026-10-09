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

/// <summary>The commissions of the resellers (ADR-063): the terms, what a payment earns, the statements and the settlement of a closed month.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class CommissionsApiTests(ApiFixture api)
{
    private static readonly Guid FirstSchedule = new("9a2e7b40-5d13-4c8f-b6a1-0f3d8e2c7a55");

    private sealed record PlanRow(Guid Id, string Code);

    private sealed record Created(Guid Id);

    private sealed record ChargeRow(Guid Id, Guid TenantId, decimal TotalAmount);

    private sealed record TierRow(int MinAccounts, decimal Rate);

    private sealed record ScheduleRow(Guid Id, int Version, DateOnly EffectiveFrom, List<TierRow> Tiers);

    private sealed record EntryRow(Guid Id, Guid TenantId, string TenantName, DateOnly ChargePeriod, Guid PaymentId, DateOnly Month, decimal BaseAmount, decimal Rate, decimal Amount);

    private sealed record SettlementRow(Guid Id, DateOnly Month, decimal Total, int Entries, DateOnly SettledOn, string? Reference);

    private sealed record MonthRow(DateOnly Month, decimal Total, int Entries, SettlementRow? Settlement);

    private sealed record StatementRow(Guid ResellerId, MonthRow Month, List<EntryRow> Items);

    private sealed record OverviewRow(Guid ResellerId, int ActiveAccounts, ScheduleRow? ScheduleForNewAccounts, decimal? CurrentRate, List<MonthRow> Months);

    private sealed record TenantRow(Guid Id, string Status, string? SuspendedBy);

    private sealed record Reseller(Guid Id, HttpClient Client);

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

    private static DateTimeOffset At(int monthsAhead, int day) => LimaCalendar.StartOf(NextMonth(monthsAhead).AddDays(day - 1)).AddHours(12);

    private async Task<Moment> AtAsync(DateTimeOffset when)
    {
        var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UsePlatform("commissions test");
        var clock = new FixedClock(when);
        var commissions = ActivatorUtilities.CreateInstance<Commissions>(scope.ServiceProvider, (TimeProvider)clock, (ICurrentUser)new PlatformStaff());
        var collections = ActivatorUtilities.CreateInstance<Collections>(scope.ServiceProvider, (TimeProvider)clock, commissions);
        return new Moment(scope, collections, commissions);
    }

    private Task<CollectionPassResult> RunAsync(DateTimeOffset when) => api.Services.GetRequiredService<ICollectionProcessor>().RunAsync(when, CancellationToken.None);

    private static async Task<PlanRow> PricedPlanAsync(HttpClient admin, decimal fee)
    {
        var created = await admin.PostAsJsonAsync("/api/v1/platform/plans", new { code = $"com-{Guid.NewGuid():N}"[..16], name = "Plan con comisión" });
        var plan = (await created.Content.ReadFromJsonAsync<PlanRow>(ApiFixture.JsonOptions))!;
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"/api/v1/platform/plans/{plan.Id}/prices", new { effectiveFrom = NextMonth(), monthlyFee = fee })).StatusCode);
        return plan;
    }

    private async Task<Reseller> NewResellerAsync(HttpClient admin)
    {
        var created = await admin.PostAsJsonAsync("/api/v1/platform/resellers", new { name = $"Canal {Guid.NewGuid():N}"[..18] });
        var id = (await created.Content.ReadFromJsonAsync<Created>(ApiFixture.JsonOptions))!.Id;
        var email = $"{Guid.NewGuid():N}@canal.test";
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "Canal", password = ApiFixture.StrongPassword, roles = new[] { Roles.ResellerAdmin }, resellerId = id })).StatusCode);
        return new Reseller(id, api.ClientFor(await api.LoginOkAsync(email, ApiFixture.StrongPassword)));
    }

    private static async Task<Guid> OpenTenantAsync(Reseller reseller, PlanRow? plan)
    {
        var opened = await reseller.Client.PostAsJsonAsync("/api/v1/reseller/tenants", new { name = $"Cliente {Guid.NewGuid():N}"[..16], environment = "Sandbox", planId = plan?.Id });
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        return (await opened.Content.ReadFromJsonAsync<Created>(ApiFixture.JsonOptions))!.Id;
    }

    private static async Task<ChargeRow> ChargeOfAsync(HttpClient admin, Guid tenantId) =>
        Assert.Single((await admin.GetFromJsonAsync<List<ChargeRow>>($"/api/v1/platform/charges?tenantId={tenantId}", ApiFixture.JsonOptions))!);

    private static RecordPaymentRequest Pay(decimal amount, DateTimeOffset when) => new(amount, PaymentMethod.Transfer, LimaCalendar.Today(when), "Operación", null);

    [Fact]
    public async Task The_commission_terms_start_with_a_default_are_published_by_the_super_admin_and_are_read_by_support()
    {
        using var admin = await api.AdminClientAsync();
        var support = await ApiFixture.CreateUserAsync(admin, Roles.PlatformSupport, null);
        using var supportClient = api.ClientFor(await api.LoginOkAsync(support.Email, support.Password));
        var reseller = await NewResellerAsync(admin);

        var schedules = (await supportClient.GetFromJsonAsync<List<ScheduleRow>>("/api/v1/platform/commission-schedules", ApiFixture.JsonOptions))!;
        var first = Assert.Single(schedules, s => s.Id == FirstSchedule);
        Assert.Equal([(0, 0.2m), (10, 0.25m), (25, 0.3m)], first.Tiers.Select(t => (t.MinAccounts, t.Rate)));

        var from = TodayInLima().AddYears(30);
        object Body(object tiers, DateOnly? date = null) => new { effectiveFrom = date ?? from, tiers };
        var ok = new[] { new { minAccounts = 0, rate = 0.15m }, new { minAccounts = 20, rate = 0.22m } };
        Assert.Equal(HttpStatusCode.Forbidden, (await supportClient.PostAsJsonAsync("/api/v1/platform/commission-schedules", Body(ok))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reseller.Client.PostAsJsonAsync("/api/v1/platform/commission-schedules", Body(ok))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reseller.Client.GetAsync("/api/v1/platform/commission-schedules")).StatusCode);

        foreach (var invalid in new object[]
        {
            Body(Array.Empty<object>()),
            Body(new[] { new { minAccounts = 1, rate = 0.2m } }),
            Body(new[] { new { minAccounts = 0, rate = 0.2m }, new { minAccounts = 0, rate = 0.3m } }),
            Body(new[] { new { minAccounts = 0, rate = 0.2m }, new { minAccounts = 10, rate = 0.25m }, new { minAccounts = 5, rate = 0.3m } }),
            Body(new[] { new { minAccounts = 0, rate = 1.5m } }),
            Body(new[] { new { minAccounts = 0, rate = -0.1m } }),
            Body(new[] { new { minAccounts = 0, rate = 0.12345m } }),
            Body(ok, TodayInLima()),
            Body(Enumerable.Range(0, 11).Select(i => new { minAccounts = i, rate = 0.1m }).ToArray()),
        })
        {
            var refused = await admin.PostAsJsonAsync("/api/v1/platform/commission-schedules", invalid);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("SF-SUB-006", await CodeAsync(refused));
        }

        var created = await admin.PostAsJsonAsync("/api/v1/platform/commission-schedules", Body(ok));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(2, (await created.Content.ReadFromJsonAsync<ScheduleRow>(ApiFixture.JsonOptions))!.Version);
        Assert.Equal("SF-SUB-006", await CodeAsync(await admin.PostAsJsonAsync("/api/v1/platform/commission-schedules", Body(ok, TodayInLima().AddYears(20)))));
    }

    [Fact]
    public async Task A_payment_earns_the_reseller_its_share_of_what_is_not_tax_with_the_terms_the_account_came_under_and_a_reversal_takes_it_back()
    {
        using var admin = await api.AdminClientAsync();
        var plan = await PricedPlanAsync(admin, 100m);
        var reseller = await NewResellerAsync(admin);
        var tenantId = await OpenTenantAsync(reseller, plan);
        await RunAsync(At(2, 2));
        var charge = await ChargeOfAsync(admin, tenantId);
        Assert.Equal(118m, charge.TotalAmount);
        var when = At(2, 3);
        var month = LimaCalendar.MonthStart(LimaCalendar.Today(when));

        await using var moment = await AtAsync(when);
        var whole = (await moment.Collections.RecordPaymentAsync(charge.Id, Pay(59m, when), CancellationToken.None)).Value;
        var second = (await moment.Collections.RecordPaymentAsync(charge.Id, Pay(59m, when), CancellationToken.None)).Value;

        // 59 of 118 is 50.00 without tax, and 20 % of it is 10.00: the whole charge earns 20.00.
        var statement = (await admin.GetFromJsonAsync<StatementRow>($"/api/v1/platform/resellers/{reseller.Id}/commissions/{month:yyyy-MM}", ApiFixture.JsonOptions))!;
        Assert.Equal((20m, 2), (statement.Month.Total, statement.Month.Entries));
        Assert.Null(statement.Month.Settlement);
        Assert.All(statement.Items, i => Assert.Equal((tenantId, 50m, 0.2m, 10m), (i.TenantId, i.BaseAmount, i.Rate, i.Amount)));

        // The terms of the account are those of the day it came under the reseller: a version published after does not reach it.
        await using var connection = new NpgsqlConnection(api.Postgres.OwnerConnectionString);
        await connection.OpenAsync();
        await using (var query = new NpgsqlCommand($"SELECT DISTINCT schedule_id FROM subscription.commission_entry WHERE tenant_id = '{tenantId}'", connection))
        {
            Assert.Equal(FirstSchedule, (Guid)(await query.ExecuteScalarAsync())!);
        }

        Assert.True((await moment.Collections.ReversePaymentAsync(second.Id, "Se registró en la cuenta equivocada", CancellationToken.None)).IsSuccess);
        var after = (await admin.GetFromJsonAsync<StatementRow>($"/api/v1/platform/resellers/{reseller.Id}/commissions/{month:yyyy-MM}", ApiFixture.JsonOptions))!;
        Assert.Equal((10m, 3), (after.Month.Total, after.Month.Entries));
        Assert.Equal(-10m, after.Items.Single(i => i.Amount < 0).Amount);
        Assert.NotEqual(whole.Id, after.Items.Single(i => i.Amount < 0).PaymentId);

        // The reseller reads its own account of it, and only it.
        var own = (await reseller.Client.GetFromJsonAsync<OverviewRow>("/api/v1/reseller/commissions", ApiFixture.JsonOptions))!;
        Assert.Equal((reseller.Id, 1, 0.2m), (own.ResellerId, own.ActiveAccounts, own.CurrentRate));
        Assert.Equal(10m, Assert.Single(own.Months).Total);
        var ownStatement = (await reseller.Client.GetFromJsonAsync<StatementRow>($"/api/v1/reseller/commissions/{month:yyyy-MM}", ApiFixture.JsonOptions))!;
        Assert.Equal(3, ownStatement.Items.Count);

        var other = await NewResellerAsync(admin);
        Assert.Empty((await other.Client.GetFromJsonAsync<OverviewRow>("/api/v1/reseller/commissions", ApiFixture.JsonOptions))!.Months);
        Assert.Empty((await other.Client.GetFromJsonAsync<StatementRow>($"/api/v1/reseller/commissions/{month:yyyy-MM}", ApiFixture.JsonOptions))!.Items);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.Client.GetAsync($"/api/v1/platform/resellers/{reseller.Id}/commissions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.Client.GetAsync($"/api/v1/platform/resellers/{reseller.Id}/commissions/{month:yyyy-MM}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await reseller.Client.GetAsync("/api/v1/reseller/commissions/octubre")).StatusCode);

        var tenantOwner = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        using var ownerClient = api.ClientFor(await api.LoginOkAsync(tenantOwner.Email, tenantOwner.Password));
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.GetAsync("/api/v1/reseller/commissions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/reseller/commissions")).StatusCode); // platform staff read through the platform routes
    }

    [Fact]
    public async Task The_rate_depends_on_how_many_active_accounts_the_reseller_has_and_an_account_without_reseller_earns_nothing()
    {
        using var admin = await api.AdminClientAsync();
        var plan = await PricedPlanAsync(admin, 100m);
        var reseller = await NewResellerAsync(admin);
        var tenants = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            tenants.Add(await OpenTenantAsync(reseller, i == 0 ? plan : null));
        }

        var direct = await api.CreateTenantAsync("Directa SAC");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{direct}/plan", new { planId = plan.Id })).StatusCode);
        await RunAsync(At(2, 2));
        var when = At(2, 3);
        await using var moment = await AtAsync(when);

        // With 10 active accounts the tier is 25 %.
        Assert.True((await moment.Collections.RecordPaymentAsync((await ChargeOfAsync(admin, tenants[0])).Id, Pay(118m, when), CancellationToken.None)).IsSuccess);
        var month = LimaCalendar.MonthStart(LimaCalendar.Today(when));
        var statement = (await admin.GetFromJsonAsync<StatementRow>($"/api/v1/platform/resellers/{reseller.Id}/commissions/{month:yyyy-MM}", ApiFixture.JsonOptions))!;
        Assert.Equal((100m, 0.25m, 25m), (statement.Items.Single().BaseAmount, statement.Items.Single().Rate, statement.Items.Single().Amount));
        Assert.Equal(0.25m, (await reseller.Client.GetFromJsonAsync<OverviewRow>("/api/v1/reseller/commissions", ApiFixture.JsonOptions))!.CurrentRate);

        // An account of nobody is paid in full and earns nothing for anyone.
        Assert.True((await moment.Collections.RecordPaymentAsync((await ChargeOfAsync(admin, direct)).Id, Pay(118m, when), CancellationToken.None)).IsSuccess);
        Assert.Single((await admin.GetFromJsonAsync<StatementRow>($"/api/v1/platform/resellers/{reseller.Id}/commissions/{month:yyyy-MM}", ApiFixture.JsonOptions))!.Items);
    }

    [Fact]
    public async Task A_closed_month_is_settled_once_for_the_total_of_its_entries_and_an_open_one_is_not()
    {
        using var admin = await api.AdminClientAsync();
        var support = await ApiFixture.CreateUserAsync(admin, Roles.PlatformSupport, null);
        using var supportClient = api.ClientFor(await api.LoginOkAsync(support.Email, support.Password));
        var plan = await PricedPlanAsync(admin, 100m);
        var reseller = await NewResellerAsync(admin);
        var tenantId = await OpenTenantAsync(reseller, plan);
        await RunAsync(At(2, 2));
        var payDay = At(2, 3);
        await using var inMonth = await AtAsync(payDay);
        Assert.True((await inMonth.Collections.RecordPaymentAsync((await ChargeOfAsync(admin, tenantId)).Id, Pay(118m, payDay), CancellationToken.None)).IsSuccess);
        var month = LimaCalendar.MonthStart(LimaCalendar.Today(payDay));
        var path = $"/api/v1/platform/resellers/{reseller.Id}/commissions/{month:yyyy-MM}/settle";
        var request = new SettleCommissionRequest(LimaCalendar.Today(At(3, 5)), "Transferencia 889", "Liquidación del mes");

        // In real time that month has not even started: it is not closed.
        var open = await admin.PostAsJsonAsync(path, new { settledOn = TodayInLima(), reference = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, open.StatusCode);
        Assert.Equal("SF-SUB-007", await CodeAsync(open));
        Assert.Equal(HttpStatusCode.Forbidden, (await supportClient.PostAsJsonAsync(path, new { settledOn = TodayInLima() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reseller.Client.PostAsJsonAsync(path, new { settledOn = TodayInLima() })).StatusCode);

        await using var closed = await AtAsync(At(3, 5));
        Assert.Equal("SF-SUB-007", (await closed.Commissions.SettleAsync(reseller.Id, LimaCalendar.MonthStart(LimaCalendar.Today(At(3, 5))), request, CancellationToken.None)).Error.Code); // the month that runs
        Assert.Equal("SF-SUB-007", (await closed.Commissions.SettleAsync(reseller.Id, month, request with { SettledOn = LimaCalendar.Today(At(2, 20)) }, CancellationToken.None)).Error.Code); // before it closed
        Assert.Equal("SF-SUB-007", (await closed.Commissions.SettleAsync(reseller.Id, month, request with { SettledOn = LimaCalendar.Today(At(3, 5)).AddDays(1) }, CancellationToken.None)).Error.Code); // future
        Assert.Equal("SF-SUB-007", (await closed.Commissions.SettleAsync(reseller.Id, month.AddMonths(-1), request, CancellationToken.None)).Error.Code); // nothing earned
        Assert.Equal("SF-SUB-007", (await closed.Commissions.SettleAsync(reseller.Id, month, request with { Reference = new string('x', 101) }, CancellationToken.None)).Error.Code);

        var settled = (await closed.Commissions.SettleAsync(reseller.Id, month, request, CancellationToken.None)).Value;
        Assert.Equal((20m, 1, "Transferencia 889"), (settled.Total, settled.Entries, settled.Reference));
        var again = await closed.Commissions.SettleAsync(reseller.Id, month, request, CancellationToken.None);
        Assert.Equal(SecureFact.SharedKernel.Results.ErrorKind.Conflict, again.Error.Kind);

        var statement = (await reseller.Client.GetFromJsonAsync<StatementRow>($"/api/v1/reseller/commissions/{month:yyyy-MM}", ApiFixture.JsonOptions))!;
        Assert.Equal(settled.Id, statement.Month.Settlement!.Id);
        Assert.Equal(settled.Id, Assert.Single((await reseller.Client.GetFromJsonAsync<OverviewRow>("/api/v1/reseller/commissions", ApiFixture.JsonOptions))!.Months).Settlement!.Id);

        var events = (await admin.GetFromJsonAsync<List<JsonElement>>("/api/v1/audit?action=subscriptions.commission.settled&take=50", ApiFixture.JsonOptions))!;
        Assert.Contains(events, e => e.GetProperty("newValues").GetRawText().Contains(reseller.Id.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reseller_cannot_lift_the_suspension_for_non_payment_that_the_platform_made()
    {
        using var admin = await api.AdminClientAsync();
        var plan = await PricedPlanAsync(admin, 100m);
        var reseller = await NewResellerAsync(admin);
        var tenantId = await OpenTenantAsync(reseller, plan);
        await RunAsync(At(2, 2));
        await RunAsync(At(2, 2).AddDays(26));

        var tenant = (await admin.GetFromJsonAsync<TenantRow>($"/api/v1/platform/tenants/{tenantId}", ApiFixture.JsonOptions))!;
        Assert.Equal(("Suspended", "NonPayment"), (tenant.Status, tenant.SuspendedBy));
        var lift = await reseller.Client.PostAsJsonAsync($"/api/v1/reseller/tenants/{tenantId}/status", new { status = "Active", reason = "Ya me pagó a mí" });
        Assert.Equal(HttpStatusCode.Forbidden, lift.StatusCode);
        Assert.Equal("SF-TEN-004", await CodeAsync(lift));
        // The platform can lift it, and it takes the suspension over if it suspends again.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/status", new { status = "Active", reason = "Acuerdo de pago" })).StatusCode);
    }
}
