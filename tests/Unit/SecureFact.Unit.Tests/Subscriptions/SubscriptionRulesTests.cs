using SecureFact.SharedKernel.Domain;
using SecureFact.Subscriptions.Application;
using SecureFact.Subscriptions.Domain;
using SecureFact.Tenancy.Contracts;

namespace SecureFact.Unit.Tests.Subscriptions;

public class SubscriptionRulesTests
{
    private static readonly Guid PlanId = Guid.NewGuid();

    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.FromHours(-5));

    private static PlanPrice Price(int version, DateOnly from, decimal fee = 100m, int? included = null, decimal? unit = null) =>
        PlanPrice.Create(PlanId, version, from, fee, included, unit, null, Now);

    private static TenantDto Tenant(Guid? resellerId = null) =>
        new(new TenantId(Guid.NewGuid()), "Cliente SAC", TenantStatus.Active, TenantEnvironment.Sandbox, resellerId, Now, PlanId, null, Now, resellerId is null ? null : Now);

    private static readonly PlanDto Plan = new(PlanId, "pro", "Profesional", null, null, null, true, null, true);

    private static BillingPolicy Policy(int dueDays = 10, int? suspendAfter = 15) => BillingPolicy.Create(1, new DateOnly(2026, 1, 1), dueDays, suspendAfter, null, Now);

    [Fact]
    public void A_plan_without_a_price_has_no_terms()
    {
        Assert.Null(TermsResolver.Resolve([], new DateOnly(2026, 10, 9)));
    }

    [Fact]
    public void A_tenant_that_took_the_plan_before_it_had_a_price_pays_the_first_version_from_the_month_it_starts()
    {
        var first = Price(1, new DateOnly(2026, 12, 1));

        var terms = TermsResolver.Resolve([first, Price(2, new DateOnly(2027, 3, 1), 150m)], new DateOnly(2026, 10, 9))!;

        Assert.Equal((1, new DateOnly(2026, 12, 1)), (terms.Price.Version, terms.FirstChargePeriod));
    }

    [Fact]
    public void A_tenant_keeps_the_version_in_force_the_day_it_took_the_plan_and_a_later_one_never_reaches_it()
    {
        var versions = new[] { Price(1, new DateOnly(2026, 1, 1)), Price(2, new DateOnly(2026, 7, 1), 130m), Price(3, new DateOnly(2027, 1, 1), 170m) };

        Assert.Equal(1, TermsResolver.Resolve(versions, new DateOnly(2026, 6, 30))!.Price.Version);
        Assert.Equal(2, TermsResolver.Resolve(versions, new DateOnly(2026, 7, 1))!.Price.Version);
        Assert.Equal(2, TermsResolver.Resolve(versions, new DateOnly(2026, 12, 31))!.Price.Version);
        Assert.Equal(3, TermsResolver.Resolve(versions, new DateOnly(2027, 1, 1))!.Price.Version);
    }

    [Theory]
    [InlineData("2026-10-01", "2026-11-01")]
    [InlineData("2026-10-31", "2026-11-01")]
    [InlineData("2026-12-15", "2027-01-01")]
    public void The_first_month_charged_is_the_one_after_the_day_the_plan_was_taken(string assigned, string expected)
    {
        var terms = TermsResolver.Resolve([Price(1, new DateOnly(2026, 1, 1))], DateOnly.Parse(assigned, System.Globalization.CultureInfo.InvariantCulture))!;

        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), terms.FirstChargePeriod);
    }

    [Fact]
    public void The_fee_is_the_charge_when_nothing_is_over_the_allowance()
    {
        var charge = CollectionPass.Build(Tenant(), Plan, Price(1, new DateOnly(2026, 10, 1), 100m, 200, 0.15m), new DateOnly(2026, 10, 1), 150, 0.18m, Policy(), new DateOnly(2026, 11, 2), null, null, Now);

        Assert.Equal((0, 0m, 100m, 18m, 118m), (charge.OverageDocuments, charge.OverageAmount, charge.NetAmount, charge.TaxAmount, charge.TotalAmount));
        Assert.Equal((150, 200), (charge.DocumentsIssued, charge.IncludedDocuments));
    }

    [Fact]
    public void Each_document_over_the_allowance_is_charged_at_the_unit_price_and_the_tax_is_rounded_once_on_the_whole()
    {
        var charge = CollectionPass.Build(Tenant(), Plan, Price(1, new DateOnly(2026, 10, 1), 33.33m, 100, 0.0850m), new DateOnly(2026, 10, 1), 233, 0.18m, Policy(), new DateOnly(2026, 11, 2), null, null, Now);

        // 133 documents over at 0.0850 are 11.305, which rounds away from zero to 11.31; 33.33 + 11.31 = 44.64; the tax 8.0352 is 8.04.
        Assert.Equal((133, 11.31m, 44.64m, 8.04m, 52.68m), (charge.OverageDocuments, charge.OverageAmount, charge.NetAmount, charge.TaxAmount, charge.TotalAmount));
        Assert.Equal(charge.NetAmount + charge.TaxAmount, charge.TotalAmount);
    }

    [Fact]
    public void A_price_without_an_overage_never_charges_extra_however_many_documents_there_were()
    {
        var charge = CollectionPass.Build(Tenant(), Plan, Price(1, new DateOnly(2026, 10, 1), 80m), new DateOnly(2026, 10, 1), 9999, 0.18m, Policy(), new DateOnly(2026, 11, 2), null, null, Now);

        Assert.Equal((0, 80m, 94.4m), (charge.OverageDocuments, charge.NetAmount, charge.TotalAmount));
    }

    [Fact]
    public void The_dates_come_from_the_policy_of_the_day_and_a_policy_that_never_suspends_leaves_no_suspension_date()
    {
        var issued = new DateOnly(2026, 11, 2);

        var suspends = CollectionPass.Build(Tenant(), Plan, Price(1, new DateOnly(2026, 10, 1)), new DateOnly(2026, 10, 1), 1, 0.18m, Policy(7, 3), issued, null, null, Now);
        var never = CollectionPass.Build(Tenant(), Plan, Price(1, new DateOnly(2026, 10, 1)), new DateOnly(2026, 10, 1), 1, 0.18m, Policy(7, null), issued, null, null, Now);
        var noPolicy = CollectionPass.Build(Tenant(), Plan, Price(1, new DateOnly(2026, 10, 1)), new DateOnly(2026, 10, 1), 1, 0.18m, null, issued, null, null, Now);

        Assert.Equal((issued, issued.AddDays(7), (DateOnly?)issued.AddDays(10)), (suspends.IssuedOn, suspends.DueOn, suspends.SuspendOn));
        Assert.Null(never.SuspendOn);
        Assert.Equal(issued, noPolicy.DueOn);
        Assert.Null(noPolicy.SuspendOn);
    }

    [Fact]
    public void The_charge_remembers_the_reseller_and_the_commission_terms_it_was_issued_under_and_the_plan_it_was_for()
    {
        var reseller = Guid.NewGuid();
        var schedule = Guid.NewGuid();

        var charge = CollectionPass.Build(Tenant(reseller), Plan, Price(1, new DateOnly(2026, 10, 1)), new DateOnly(2026, 10, 1), 1, 0.18m, Policy(), new DateOnly(2026, 11, 2), schedule, reseller, Now);

        Assert.Equal((reseller, schedule, "pro", "Profesional", "Cliente SAC", "PEN"), (charge.ResellerId, charge.CommissionScheduleId, charge.PlanCode, charge.PlanName, charge.TenantName, charge.Currency));
        Assert.Equal(new DateOnly(2026, 10, 1), charge.Period);
    }

    [Fact]
    public void A_charge_can_be_voided_once_with_its_reason()
    {
        var charge = CollectionPass.Build(Tenant(), Plan, Price(1, new DateOnly(2026, 10, 1)), new DateOnly(2026, 10, 1), 1, 0.18m, Policy(), new DateOnly(2026, 11, 2), null, null, Now);
        Assert.False(charge.IsVoid);

        charge.MarkVoid("Duplicado", Now);

        Assert.True(charge.IsVoid);
        Assert.Equal("Duplicado", charge.VoidReason);
    }

    [Theory]
    [InlineData("2026-11-01T04:59:59Z", "2026-10-31")]
    [InlineData("2026-11-01T05:00:00Z", "2026-11-01")]
    [InlineData("2026-12-31T23:59:59Z", "2026-12-31")]
    public void The_day_is_the_day_in_Lima(string instant, string day)
    {
        Assert.Equal(
            DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture),
            LimaCalendar.Today(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void A_day_starts_at_five_in_the_morning_universal_time_all_year_and_a_period_has_a_name()
    {
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 5, 0, 0, TimeSpan.Zero), LimaCalendar.StartOf(new DateOnly(2026, 3, 1)));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 5, 0, 0, TimeSpan.Zero), LimaCalendar.StartOf(new DateOnly(2026, 9, 1)));
        Assert.Equal(new DateOnly(2026, 2, 1), LimaCalendar.MonthStart(new DateOnly(2026, 2, 17)));
        Assert.Equal("2026-02", LimaCalendar.PeriodName(new DateOnly(2026, 2, 1)));
    }
}
