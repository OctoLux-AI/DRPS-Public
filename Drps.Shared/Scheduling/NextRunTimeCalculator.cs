namespace Drps.Shared.Scheduling;

/// <summary>
/// Pure, deterministic "next fixed daily time" calculation - no DI/clock dependency beyond
/// the <paramref name="now"/> parameter passed to <see cref="GetNextRunTime(DateTime)"/> or
/// <see cref="GetNextRunTime(DateTime, TimeSpan)"/>, so it's directly unit-testable. Always
/// recomputed against the current wall-clock time on every call (never a running total added
/// to the previous run's timestamp), so a scheduling loop built on this can't drift across
/// process restarts or a missed cycle - it always finds the real next occurrence of the target
/// time from wherever "now" actually is.
///
/// Moved here from Drps.Ingestion (scheduling audit/fix, this session) once Drps.Calculator and
/// Drps.Gate needed the identical mechanism for their own once-daily loops - per this
/// codebase's own Anti-Spaghetti rule ("shared types go in Drps.Shared, if more than one
/// project needs it"), rather than either project reimplementing a second copy or taking on a
/// heavier ProjectReference just to reach one static utility. ExDividendWorker/SectorWorker
/// (Drps.Ingestion) are behaviorally unaffected by this move - only their `using` changed to
/// point at this namespace.
/// </summary>
public static class NextRunTimeCalculator
{
    public static readonly TimeSpan DailyRunTime = TimeSpan.FromHours(3);

    /// <summary>
    /// Today's 03:00 if it hasn't passed yet (including the exact instant of 03:00 itself -
    /// a scheduler that's precisely on time should fire now, not wait a full extra day).
    /// Otherwise, tomorrow's 03:00. Equivalent to <c>GetNextRunTime(now, DailyRunTime)</c>.
    /// </summary>
    public static DateTime GetNextRunTime(DateTime now) => GetNextRunTime(now, DailyRunTime);

    /// <summary>
    /// Same rule as the single-argument overload, against an explicit <paramref
    /// name="runTime"/> of day instead of the default 03:00. Added so multiple daily loops can
    /// each target a different fixed time using the identical calculation - e.g. Drps.Ingestion
    /// at 03:00, Drps.Calculator at 03:20, Drps.Gate at 03:35, staggered so each pipeline stage
    /// has a real buffer to finish before the next one starts (a time-offset guarantee, not a
    /// completion guarantee - see each Worker's own doc comment for that limitation).
    /// </summary>
    public static DateTime GetNextRunTime(DateTime now, TimeSpan runTime)
    {
        var todayAtRunTime = now.Date + runTime;
        return now <= todayAtRunTime ? todayAtRunTime : todayAtRunTime.AddDays(1);
    }

    /// <summary>
    /// Same fixed-time rule as the daily overloads above, but for a fixed day-of-week cadence -
    /// e.g. Drps.Ingestion's weekly data-quality audit (CLAUDE.md's "Weekly Data-Quality Audit:
    /// Alpaca vs. Tiingo Variance", 2026-07-22), which must fire once every 7 days, never more
    /// often, even across a process restart that happens to land on the target day. If today
    /// already is <paramref name="targetDayOfWeek"/> and <paramref name="runTime"/> hasn't
    /// passed yet, fires today (same "&lt;=" boundary as the daily overloads); otherwise the
    /// next occurrence is a full 7 days out, never "tomorrow" the way the daily overload would
    /// treat a missed same-day time.
    /// </summary>
    public static DateTime GetNextWeeklyRunTime(DateTime now, DayOfWeek targetDayOfWeek, TimeSpan runTime)
    {
        var daysUntilTarget = ((int)targetDayOfWeek - (int)now.DayOfWeek + 7) % 7;
        var candidate = now.Date.AddDays(daysUntilTarget) + runTime;
        return candidate >= now ? candidate : candidate.AddDays(7);
    }

    /// <summary>
    /// The most recent date on or before <paramref name="date"/> falling on <paramref
    /// name="targetDayOfWeek"/> - e.g. resolving "this week's Friday" (the last trading day)
    /// from a Saturday-scheduled job's own run date. Returns <paramref name="date"/> itself
    /// when it already falls on the target day.
    /// </summary>
    public static DateOnly GetMostRecentOccurrence(DateOnly date, DayOfWeek targetDayOfWeek)
    {
        var daysSinceTarget = ((int)date.DayOfWeek - (int)targetDayOfWeek + 7) % 7;
        return date.AddDays(-daysSinceTarget);
    }
}
