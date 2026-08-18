namespace Drps.Shared.Scoring;

/// <summary>
/// Shared weekday-only trading-calendar helper - the same deliberate simplification (no
/// market-holiday calendar) NoBuyExpirationCalculator already documents for its own session-
/// counting rule. Extracted as its own shared type so Gate's plateau reassessment trigger
/// (2026-07-18 decision block, point 3's "5 trading days elapsed" condition) uses the identical
/// weekday definition rather than a second, potentially-drifting copy of the same pattern.
/// </summary>
public static class TradingDayCalendar
{
    public static bool IsWeekday(DateTime date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <summary>
    /// Counts full trading days strictly after <paramref name="fromDate"/> up to and including
    /// <paramref name="toDate"/> - i.e. how many weekdays have elapsed since fromDate. Returns 0
    /// when toDate is on or before fromDate (dates normalized to their Date component first, so
    /// a same-day comparison is never skewed by a time-of-day difference).
    /// </summary>
    public static int CountElapsedTradingDays(DateTime fromDate, DateTime toDate)
    {
        var date = fromDate.Date;
        var end = toDate.Date;
        var count = 0;

        while (date < end)
        {
            date = date.AddDays(1);
            if (IsWeekday(date))
            {
                count++;
            }
        }

        return count;
    }
}
