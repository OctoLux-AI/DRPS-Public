namespace Drps.Shared.Scoring;

/// <summary>
/// Shared NoBuy-list expiration rule (Gate: Seventh Design Decision, Revised Again's
/// 2-full-trading-session rule): a mid-session trigger does not count that partial session, so
/// the count starts the day after the trigger date. Extracted out of GateCompositeService so
/// Drps.Ledger's LedgerPositionStateProvider can compute the identical expiration from a
/// stored CoolDownStartDate rather than re-implementing the rule a second time.
///
/// A simple weekday-only calendar (no market-holiday calendar) is a known, deliberate
/// simplification carried over from the original GateCompositeService implementation - not a
/// real trading-calendar implementation.
/// </summary>
public static class NoBuyExpirationCalculator
{
    public static DateTime ComputeExpiration(DateTime triggerDate, int noBuySessionCount)
    {
        var date = triggerDate.Date;
        var sessionsElapsed = 0;

        while (sessionsElapsed < noBuySessionCount)
        {
            date = date.AddDays(1);
            if (IsWeekday(date))
            {
                sessionsElapsed++;
            }
        }

        // date now sits on the last required full session (e.g. Friday) - advance to the next
        // weekday, the session on which the ticker actually becomes tradeable again (e.g.
        // Monday).
        do
        {
            date = date.AddDays(1);
        } while (!IsWeekday(date));

        return date;
    }

    private static bool IsWeekday(DateTime date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
}
