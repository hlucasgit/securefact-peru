using Microsoft.EntityFrameworkCore;
using SecureFact.Notifications.Contracts;
using SecureFact.Subscriptions.Domain;
using SecureFact.Subscriptions.Infrastructure;

namespace SecureFact.Subscriptions.Application;

/// <summary>
/// The notices of the daily pass for the charges that are still unpaid (ADR-068): one that falls due soon, one that is overdue, one whose account is near its suspension. Each pass tells a charge
/// the most urgent thing that is true that day; the notice queue tells each fact once per address, so running the pass every day, or twice a day, never repeats a notice.
/// </summary>
internal sealed class BillingReminders(SubscriptionsDbContext db, IBillingNotices notices)
{
    /// <summary>The most unpaid charges that one pass looks at, the ones that fall due last first, so that an old charge that nobody pays does not take the place of the recent ones.</summary>
    private const int MaxCandidates = 1000;

    /// <summary>
    /// What a charge that is unpaid, not void and issued before <paramref name="today"/> has to be told on <paramref name="today"/>, or null. From the day of the suspension on, the enforcement acts and
    /// the account is told by its change of status, so there is no collection notice any more.
    /// </summary>
    /// <param name="reminderDays">Days of notice before the due date and before the suspension, from the billing policy; 0 sends no advance notice.</param>
    internal static BillingNoticeKind? KindFor(DateOnly dueOn, DateOnly? suspendOn, DateOnly today, int reminderDays)
    {
        if (suspendOn is { } suspension && today >= suspension)
        {
            return null;
        }

        if (today > dueOn)
        {
            return reminderDays > 0 && suspendOn is { } on && today >= on.AddDays(-reminderDays) ? BillingNoticeKind.SuspensionNear : BillingNoticeKind.Overdue;
        }

        return reminderDays > 0 && today >= dueOn.AddDays(-reminderDays) ? BillingNoticeKind.DueSoon : null;
    }

    /// <returns>The number of notices that were queued.</returns>
    public async Task<int> RunAsync(IReadOnlySet<Guid> notifiable, int reminderDays, DateOnly today, CancellationToken cancellationToken)
    {
        var candidates = await db.Charges.AsNoTracking()
            .Where(c => c.VoidedAt == null && c.IssuedOn < today && (c.SuspendOn == null || c.SuspendOn > today))
            .Select(c => new { Charge = c, Paid = db.Payments.Where(p => p.ChargeId == c.Id).Sum(p => (decimal?)p.Amount) ?? 0m })
            .Where(r => r.Paid < r.Charge.TotalAmount)
            .OrderByDescending(r => r.Charge.DueOn)
            .ThenBy(r => r.Charge.Id)
            .Take(MaxCandidates)
            .ToListAsync(cancellationToken);

        var queued = 0;
        foreach (var row in candidates.Where(r => notifiable.Contains(r.Charge.TenantId)))
        {
            if (KindFor(row.Charge.DueOn, row.Charge.SuspendOn, today, reminderDays) is { } kind && await notices.SendAsync(Notice(kind, row.Charge, row.Paid), cancellationToken))
            {
                queued++;
            }
        }

        return queued;
    }

    internal static BillingNotice Notice(BillingNoticeKind kind, Charge charge, decimal paid) =>
        new(kind, charge.TenantId, charge.Id, LimaCalendar.PeriodName(charge.Period), charge.TotalAmount, charge.TotalAmount - paid, charge.Currency, charge.DueOn, charge.SuspendOn);
}
