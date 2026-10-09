namespace SecureFact.Subscriptions.Application;

/// <summary>Months and days are those of Lima, as in the monthly count of the plans (ADR-042). Lima has no daylight saving time, so a day starts at the same instant all year.</summary>
internal static class LimaCalendar
{
    private static readonly TimeZoneInfo Lima = TimeZoneInfo.FindSystemTimeZoneById("America/Lima");

    public static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Lima).DateTime);

    public static DateOnly MonthStart(DateOnly date) => new(date.Year, date.Month, 1);

    /// <summary>The instant at which a day starts in Lima.</summary>
    public static DateTimeOffset StartOf(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, Lima.GetUtcOffset(local)).ToUniversalTime();
    }

    public static string PeriodName(DateOnly period) => period.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
}
