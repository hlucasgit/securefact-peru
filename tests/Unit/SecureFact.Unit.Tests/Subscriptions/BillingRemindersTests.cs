using SecureFact.Notifications.Contracts;
using SecureFact.Subscriptions.Application;

namespace SecureFact.Unit.Tests.Subscriptions;

/// <summary>The calendar of the collection notices (ADR-068): what an unpaid charge is told on each day, as a function of its dates and of the days of notice of the policy.</summary>
public class BillingRemindersTests
{
    private static readonly DateOnly Month = new(2026, 11, 1);

    private static DateOnly Day(int day) => Month.AddDays(day - 1);

    // Due on the 12th, suspension on the 27th, three days of notice.
    [Theory]
    [InlineData(3, null)]
    [InlineData(8, null)]
    [InlineData(9, BillingNoticeKind.DueSoon)]
    [InlineData(12, BillingNoticeKind.DueSoon)]
    [InlineData(13, BillingNoticeKind.Overdue)]
    [InlineData(23, BillingNoticeKind.Overdue)]
    [InlineData(24, BillingNoticeKind.SuspensionNear)]
    [InlineData(26, BillingNoticeKind.SuspensionNear)]
    [InlineData(27, null)]
    [InlineData(30, null)]
    public void A_charge_with_a_suspension_is_told_by_the_calendar_of_its_dates(int day, BillingNoticeKind? expected) =>
        Assert.Equal(expected, BillingReminders.KindFor(Day(12), Day(27), Day(day), 3));

    [Theory]
    [InlineData(9, null)]
    [InlineData(12, null)]
    [InlineData(13, BillingNoticeKind.Overdue)]
    [InlineData(26, BillingNoticeKind.Overdue)]
    [InlineData(27, null)]
    public void Zero_days_of_notice_leave_only_the_overdue_notice(int day, BillingNoticeKind? expected) =>
        Assert.Equal(expected, BillingReminders.KindFor(Day(12), Day(27), Day(day), 0));

    [Theory]
    [InlineData(8, null)]
    [InlineData(9, BillingNoticeKind.DueSoon)]
    [InlineData(13, BillingNoticeKind.Overdue)]
    [InlineData(28, BillingNoticeKind.Overdue)]
    public void A_policy_that_never_suspends_has_no_near_suspension_notice(int day, BillingNoticeKind? expected) =>
        Assert.Equal(expected, BillingReminders.KindFor(Day(12), null, Day(day), 3));

    [Theory]
    [InlineData(11, BillingNoticeKind.DueSoon)]
    [InlineData(12, BillingNoticeKind.DueSoon)]
    [InlineData(13, BillingNoticeKind.SuspensionNear)]
    [InlineData(14, null)]
    public void A_short_grace_goes_from_the_due_date_to_the_near_suspension_without_an_overdue_notice(int day, BillingNoticeKind? expected) =>
        Assert.Equal(expected, BillingReminders.KindFor(Day(12), Day(14), Day(day), 3));

    [Fact]
    public void The_notice_of_a_charge_that_was_not_paid_by_the_policy_date_names_the_balance_and_the_period()
    {
        var charge = SecureFact.Subscriptions.Domain.Charge.Create(
            Guid.NewGuid(), "Cliente SAC", new DateOnly(2026, 10, 1), Guid.NewGuid(), "pro", "Profesional", Guid.NewGuid(), 100m, null, 0, 0, null, 0m, 100m, 0.18m, 18m, 118m, Day(2), Day(12), Day(27), null, null, DateTimeOffset.UtcNow);

        var notice = BillingReminders.Notice(BillingNoticeKind.Overdue, charge, 50m);

        Assert.Equal((BillingNoticeKind.Overdue, charge.TenantId, charge.Id, "2026-10", 118m, 68m, "PEN"), (notice.Kind, notice.TenantId, notice.ChargeId, notice.Period, notice.Total, notice.Balance, notice.Currency));
        Assert.Equal((Day(12), (DateOnly?)Day(27), (Guid?)null), (notice.DueOn, notice.SuspendOn, notice.PaymentId));
    }
}
