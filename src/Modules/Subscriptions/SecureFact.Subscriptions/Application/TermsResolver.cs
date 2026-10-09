using SecureFact.Subscriptions.Domain;

namespace SecureFact.Subscriptions.Application;

/// <summary>
/// Which price a tenant keeps and from which month it is charged. It is a function of the day the tenant took its plan and of the versions published: no state is stored, so it cannot drift, and a
/// version published later never reaches a tenant that took the plan before it (ADR-062).
/// </summary>
internal static class TermsResolver
{
    internal sealed record Terms(PlanPrice Price, DateOnly FirstChargePeriod);

    /// <param name="versions">The versions of the price of the plan, oldest first.</param>
    /// <param name="assignedOn">The day (Lima) that the tenant took the plan.</param>
    /// <returns>
    /// The version in force that day or, when the plan had no price yet, its first version (an account that was on the plan before the plan had a price pays its first one). The first month charged is
    /// the one after the day the plan was taken, never before the price is in force. Null while the plan has no price.
    /// </returns>
    public static Terms? Resolve(IReadOnlyList<PlanPrice> versions, DateOnly assignedOn)
    {
        if (versions.Count == 0)
        {
            return null;
        }

        var price = versions.LastOrDefault(v => v.EffectiveFrom <= assignedOn) ?? versions[0];
        var nextMonth = LimaCalendar.MonthStart(assignedOn).AddMonths(1);
        return new Terms(price, nextMonth > price.EffectiveFrom ? nextMonth : price.EffectiveFrom);
    }
}
