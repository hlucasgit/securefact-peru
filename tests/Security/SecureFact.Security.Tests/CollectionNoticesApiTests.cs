using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SecureFact.Identity.Contracts;
using SecureFact.Platform.Tenancy;
using SecureFact.SharedKernel.Domain;
using SecureFact.SharedKernel.Tenancy;
using SecureFact.Subscriptions.Application;
using SecureFact.Subscriptions.Contracts;

namespace SecureFact.Security.Tests;

/// <summary>The collection notices (ADR-068): what the owners of an account are told about its charges and payments, when, and never twice.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class CollectionNoticesApiTests(ApiFixture api)
{
    private sealed record PlanRow(Guid Id);

    private sealed record ChargeRow(Guid Id, Guid TenantId, DateOnly Period, decimal TotalAmount, DateOnly IssuedOn, DateOnly DueOn, DateOnly? SuspendOn);

    private sealed record PaymentRow(Guid Id);

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

    private static DateOnly NextMonth(int months = 1) => LimaCalendar.MonthStart(LimaCalendar.Today(DateTimeOffset.UtcNow)).AddMonths(months);

    private static DateTimeOffset At(int monthsAhead, int day) => LimaCalendar.StartOf(NextMonth(monthsAhead).AddDays(day - 1)).AddHours(12);

    private static DateTimeOffset NoonOf(DateOnly date) => LimaCalendar.StartOf(date).AddHours(12);

    private static string Day(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private Task<CollectionPassResult> RunAsync(DateTimeOffset when) =>
        api.Services.GetRequiredService<ICollectionProcessor>().RunAsync(when, CancellationToken.None);

    private async Task<(Guid TenantId, string Email, ChargeRow Charge)> ChargedTenantAsync(HttpClient admin, string name, decimal fee)
    {
        var plan = (await (await admin.PostAsJsonAsync("/api/v1/platform/plans", new { code = $"nt-{Guid.NewGuid():N}"[..16], name = "Plan con avisos" })).Content.ReadFromJsonAsync<PlanRow>(ApiFixture.JsonOptions))!;
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"/api/v1/platform/plans/{plan.Id}/prices", new { effectiveFrom = NextMonth(), monthlyFee = fee })).StatusCode);
        var tenantId = await api.CreateTenantAsync($"{name} {Guid.NewGuid():N}"[..28]);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/platform/tenants/{tenantId}/plan", new { planId = plan.Id })).StatusCode);
        var owner = await ApiFixture.CreateUserAsync(admin, Roles.TenantOwner, tenantId);
        await RunAsync(At(2, 2));
        var charge = (await admin.GetFromJsonAsync<List<ChargeRow>>($"/api/v1/platform/charges?tenantId={tenantId}", ApiFixture.JsonOptions))!.Single();
        return (tenantId, owner.Email, charge);
    }

    /// <summary>The collection notices that an address received, in the order they were sent.</summary>
    private async Task<List<string>> NoticesAsync(string email)
    {
        await api.DrainMailAsync();
        return api.Mail.To(email).Select(m => m.Subject)
            .Where(s => s.StartsWith("Cargo de ", StringComparison.Ordinal) || s.StartsWith("Su cargo de ", StringComparison.Ordinal) || s.StartsWith("Su cuenta se suspende", StringComparison.Ordinal) || s.StartsWith("Recibimos su pago", StringComparison.Ordinal))
            .ToList();
    }

    private async Task<(AsyncServiceScope Scope, Collections Collections)> AtAsync(DateTimeOffset when)
    {
        var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<DataScope>().UsePlatform("collection notices test");
        var clock = new FixedClock(when);
        var commissions = ActivatorUtilities.CreateInstance<Commissions>(scope.ServiceProvider, (TimeProvider)clock, (ICurrentUser)new PlatformStaff());
        return (scope, ActivatorUtilities.CreateInstance<Collections>(scope.ServiceProvider, (TimeProvider)clock, commissions));
    }

    [Fact]
    public async Task The_owners_are_told_of_a_new_charge_with_its_dates_once_and_only_the_owners_of_that_account()
    {
        using var admin = await api.AdminClientAsync();
        var (_, email, charge) = await ChargedTenantAsync(admin, "Aviso de cargo", 100m);
        var (_, other, _) = await ChargedTenantAsync(admin, "Aviso ajeno", 200m);

        var notices = await NoticesAsync(email);
        var subject = Assert.Single(notices);
        Assert.Contains("S/ 118.00", subject, StringComparison.Ordinal); // 100 plus the IGV
        Assert.Contains($"vence el {Day(charge.DueOn)}", subject, StringComparison.Ordinal);
        var message = Assert.Single(api.Mail.To(email), m => m.Subject == subject);
        Assert.Contains($"la cuenta se suspende el {Day(charge.SuspendOn!.Value)}", message.Text, StringComparison.Ordinal);
        Assert.Contains($"{ApiFixture.PublicUrl}/plan", message.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("S/ 236.00", message.Text, StringComparison.Ordinal); // the charge of another account
        Assert.Contains("S/ 236.00", Assert.Single(await NoticesAsync(other)), StringComparison.Ordinal);

        await RunAsync(At(2, 2).AddHours(1));
        Assert.Single(await NoticesAsync(email));
    }

    [Fact]
    public async Task An_unpaid_charge_is_told_at_its_due_date_when_overdue_and_when_the_suspension_is_near_each_once_and_nothing_after_the_suspension()
    {
        using var admin = await api.AdminClientAsync();
        var (tenantId, email, charge) = await ChargedTenantAsync(admin, "Aviso de mora", 100m);
        var due = charge.DueOn;
        var suspend = charge.SuspendOn!.Value;

        await RunAsync(NoonOf(due.AddDays(-4)));
        Assert.Single(await NoticesAsync(email)); // only the charge itself: it is not yet the time

        await RunAsync(NoonOf(due.AddDays(-3)));
        await RunAsync(NoonOf(due));
        var soon = (await NoticesAsync(email)).Where(s => s.StartsWith("Su cargo de ", StringComparison.Ordinal)).ToList();
        Assert.Equal($"Su cargo de {Month(charge.Period)} vence el {Day(due)}", Assert.Single(soon));

        await RunAsync(NoonOf(due.AddDays(1)));
        await RunAsync(NoonOf(due.AddDays(2)));
        Assert.Single(await NoticesAsync(email), s => s.EndsWith("está vencido", StringComparison.Ordinal));
        var overdue = Assert.Single(api.Mail.To(email), m => m.Subject.EndsWith("está vencido", StringComparison.Ordinal));
        Assert.Contains("Saldo pendiente: S/ 118.00", overdue.Text, StringComparison.Ordinal);

        await RunAsync(NoonOf(suspend.AddDays(-3)));
        await RunAsync(NoonOf(suspend.AddDays(-1)));
        Assert.Single(await NoticesAsync(email), s => s.StartsWith("Su cuenta se suspende el " + Day(suspend), StringComparison.Ordinal));

        var told = (await NoticesAsync(email)).Count;
        Assert.Equal(4, told); // the charge, the due date, the overdue charge and the near suspension

        await RunAsync(NoonOf(suspend));
        await RunAsync(NoonOf(suspend.AddDays(1)));
        Assert.Equal("Suspended", (await admin.GetFromJsonAsync<TenantRow>($"/api/v1/platform/tenants/{tenantId}", ApiFixture.JsonOptions))!.Status);
        Assert.Equal(told, (await NoticesAsync(email)).Count); // the suspension is told by the change of status, not by one more collection notice
        Assert.Contains(api.Mail.To(email), m => m.Subject.Contains("fue suspendida", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_payment_is_told_with_what_is_still_owed_and_a_paid_charge_gets_no_more_reminders()
    {
        using var admin = await api.AdminClientAsync();
        var (_, email, charge) = await ChargedTenantAsync(admin, "Aviso de pago", 100m);
        var paidOn = LimaCalendar.Today(At(2, 3));

        var (scope, collections) = await AtAsync(At(2, 3));
        await using (scope)
        {
            var first = await collections.RecordPaymentAsync(charge.Id, new RecordPaymentRequest(50m, PaymentMethod.Transfer, paidOn, "Operación 1", null), CancellationToken.None);
            Assert.True(first.IsSuccess);
            var partial = Assert.Single(await NoticesAsync(email), s => s.StartsWith("Recibimos su pago", StringComparison.Ordinal));
            Assert.Equal("Recibimos su pago de S/ 50.00", partial);
            Assert.Contains($"Saldo pendiente del cargo: S/ 68.00, que vence el {Day(charge.DueOn)}", api.Mail.To(email).Single(m => m.Subject == partial).Text, StringComparison.Ordinal);

            Assert.True((await collections.RecordPaymentAsync(charge.Id, new RecordPaymentRequest(68m, PaymentMethod.Transfer, paidOn, "Operación 2", null), CancellationToken.None)).IsSuccess);
            var notices = await NoticesAsync(email);
            Assert.Contains("Recibimos su pago de S/ 68.00", notices);
            Assert.Contains("El cargo quedó pagado.", api.Mail.To(email).Single(m => m.Subject == "Recibimos su pago de S/ 68.00").Text, StringComparison.Ordinal);

            // A paid charge is not reminded, whatever the date.
            await RunAsync(NoonOf(charge.DueOn.AddDays(-2)));
            await RunAsync(NoonOf(charge.DueOn.AddDays(3)));
            Assert.Equal(notices.Count, (await NoticesAsync(email)).Count);

            // Undoing a payment is an accounting correction of the platform, not news for the account.
            Assert.True((await collections.ReversePaymentAsync(first.Value.Id, "Depósito mal aplicado", CancellationToken.None)).IsSuccess);
            Assert.Equal(notices.Count, (await NoticesAsync(email)).Count);
        }
    }

    private static readonly string[] MonthNames = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    private static string Month(DateOnly period) => $"{MonthNames[period.Month - 1]} de {period.Year.ToString(CultureInfo.InvariantCulture)}";

    private sealed record TenantRow(Guid Id, string Status);
}
