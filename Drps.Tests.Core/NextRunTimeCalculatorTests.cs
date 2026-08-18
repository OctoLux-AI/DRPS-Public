using Drps.Shared.Scheduling;

namespace Drps.Tests;

public class NextRunTimeCalculatorTests
{
    [Fact]
    public void GetNextRunTime_BeforeThreeAmToday_ReturnsTodayAtThreeAm()
    {
        var now = new DateTime(2026, 7, 15, 1, 30, 0);

        var next = NextRunTimeCalculator.GetNextRunTime(now);

        Assert.Equal(new DateTime(2026, 7, 15, 3, 0, 0), next);
    }

    [Fact]
    public void GetNextRunTime_AfterThreeAmToday_ReturnsTomorrowAtThreeAm()
    {
        var now = new DateTime(2026, 7, 15, 14, 45, 0);

        var next = NextRunTimeCalculator.GetNextRunTime(now);

        Assert.Equal(new DateTime(2026, 7, 16, 3, 0, 0), next);
    }

    [Fact]
    public void GetNextRunTime_ExactlyAtThreeAm_ReturnsTodayAtThreeAmWithZeroDelay()
    {
        var now = new DateTime(2026, 7, 15, 3, 0, 0);

        var next = NextRunTimeCalculator.GetNextRunTime(now);

        // A scheduler that's precisely on time should fire now, not wait a full extra day -
        // this is the deliberate <= boundary choice, not an arbitrary pick.
        Assert.Equal(new DateTime(2026, 7, 15, 3, 0, 0), next);
        Assert.Equal(TimeSpan.Zero, next - now);
    }

    [Fact]
    public void GetNextRunTime_OneMillisecondAfterThreeAm_ReturnsTomorrowAtThreeAm()
    {
        var now = new DateTime(2026, 7, 15, 3, 0, 0).AddMilliseconds(1);

        var next = NextRunTimeCalculator.GetNextRunTime(now);

        Assert.Equal(new DateTime(2026, 7, 16, 3, 0, 0), next);
    }

    [Fact]
    public void GetNextRunTime_JustBeforeMidnight_ReturnsThreeAmLaterThatSameCalendarDay()
    {
        var now = new DateTime(2026, 7, 15, 23, 59, 0);

        var next = NextRunTimeCalculator.GetNextRunTime(now);

        Assert.Equal(new DateTime(2026, 7, 16, 3, 0, 0), next);
    }

    // GetNextRunTime(now, runTime) overload - added so Drps.Calculator (03:20) and Drps.Gate
    // (03:35) can each target their own fixed daily time using the identical calculation,
    // rather than the default 03:00 the single-argument overload above always uses.

    [Fact]
    public void GetNextRunTime_WithExplicitRunTime_BeforeTargetToday_ReturnsTodayAtTarget()
    {
        var now = new DateTime(2026, 7, 15, 3, 10, 0);

        var next = NextRunTimeCalculator.GetNextRunTime(now, new TimeSpan(3, 20, 0));

        Assert.Equal(new DateTime(2026, 7, 15, 3, 20, 0), next);
    }

    [Fact]
    public void GetNextRunTime_WithExplicitRunTime_AfterTargetToday_ReturnsTomorrowAtTarget()
    {
        var now = new DateTime(2026, 7, 15, 3, 25, 0);

        var next = NextRunTimeCalculator.GetNextRunTime(now, new TimeSpan(3, 20, 0));

        Assert.Equal(new DateTime(2026, 7, 16, 3, 20, 0), next);
    }

    [Fact]
    public void GetNextRunTime_WithExplicitRunTime_ExactlyAtTarget_ReturnsTodayWithZeroDelay()
    {
        var now = new DateTime(2026, 7, 15, 3, 35, 0);

        var next = NextRunTimeCalculator.GetNextRunTime(now, new TimeSpan(3, 35, 0));

        Assert.Equal(new DateTime(2026, 7, 15, 3, 35, 0), next);
        Assert.Equal(TimeSpan.Zero, next - now);
    }

    [Fact]
    public void GetNextRunTime_NoRunTimeArgument_MatchesExplicitDailyRunTimeConstant()
    {
        // Confirms the single-argument overload is genuinely equivalent to passing
        // DailyRunTime explicitly, not a second, independently-drifting calculation.
        var now = new DateTime(2026, 7, 15, 1, 30, 0);

        var viaDefault = NextRunTimeCalculator.GetNextRunTime(now);
        var viaExplicit = NextRunTimeCalculator.GetNextRunTime(now, NextRunTimeCalculator.DailyRunTime);

        Assert.Equal(viaExplicit, viaDefault);
    }

    // GetNextWeeklyRunTime - added for Drps.Ingestion's weekly (not nightly) data-quality
    // audit (CLAUDE.md, "Weekly Data-Quality Audit: Alpaca vs. Tiingo Variance", 2026-07-22).
    // 2026-07-18 is a Saturday, 2026-07-17 a Friday, 2026-07-15 a Wednesday - confirmed against
    // the calendar, not assumed.

    [Fact]
    public void GetNextWeeklyRunTime_BeforeTargetDayInSameWeek_ReturnsThisWeeksTargetDay()
    {
        var now = new DateTime(2026, 7, 15, 12, 0, 0); // Wednesday

        var next = NextRunTimeCalculator.GetNextWeeklyRunTime(now, DayOfWeek.Saturday, new TimeSpan(4, 0, 0));

        Assert.Equal(new DateTime(2026, 7, 18, 4, 0, 0), next); // this week's Saturday
    }

    [Fact]
    public void GetNextWeeklyRunTime_OnTargetDayBeforeRunTime_ReturnsTodayAtRunTime()
    {
        var now = new DateTime(2026, 7, 18, 3, 0, 0); // Saturday, before 04:00

        var next = NextRunTimeCalculator.GetNextWeeklyRunTime(now, DayOfWeek.Saturday, new TimeSpan(4, 0, 0));

        Assert.Equal(new DateTime(2026, 7, 18, 4, 0, 0), next);
    }

    [Fact]
    public void GetNextWeeklyRunTime_ExactlyAtTargetTimeOnTargetDay_ReturnsTodayWithZeroDelay()
    {
        var now = new DateTime(2026, 7, 18, 4, 0, 0); // Saturday, exactly 04:00

        var next = NextRunTimeCalculator.GetNextWeeklyRunTime(now, DayOfWeek.Saturday, new TimeSpan(4, 0, 0));

        Assert.Equal(new DateTime(2026, 7, 18, 4, 0, 0), next);
        Assert.Equal(TimeSpan.Zero, next - now);
    }

    [Fact]
    public void GetNextWeeklyRunTime_OnTargetDayAfterRunTime_ReturnsNextWeekNotTomorrow()
    {
        var now = new DateTime(2026, 7, 18, 4, 0, 1); // Saturday, one second after 04:00

        var next = NextRunTimeCalculator.GetNextWeeklyRunTime(now, DayOfWeek.Saturday, new TimeSpan(4, 0, 0));

        // Must be a full 7 days out, not "tomorrow" - a missed weekly slot is not a daily one.
        Assert.Equal(new DateTime(2026, 7, 25, 4, 0, 0), next);
    }

    [Fact]
    public void GetNextWeeklyRunTime_AfterTargetDayInSameWeek_ReturnsNextWeeksTargetDay()
    {
        var now = new DateTime(2026, 7, 19, 12, 0, 0); // Sunday, target day (Saturday) already passed this week

        var next = NextRunTimeCalculator.GetNextWeeklyRunTime(now, DayOfWeek.Saturday, new TimeSpan(4, 0, 0));

        Assert.Equal(new DateTime(2026, 7, 25, 4, 0, 0), next); // next Saturday
    }

    // GetMostRecentOccurrence - resolves a scheduled job's own run date back to "this week's
    // Friday" (the week-ending anchor WeeklyVarianceAuditService actually operates on).

    [Fact]
    public void GetMostRecentOccurrence_DateIsAfterTargetDayInSameWeek_ReturnsThatWeeksTargetDay()
    {
        var saturday = new DateOnly(2026, 7, 18);

        var result = NextRunTimeCalculator.GetMostRecentOccurrence(saturday, DayOfWeek.Friday);

        Assert.Equal(new DateOnly(2026, 7, 17), result); // the day before
    }

    [Fact]
    public void GetMostRecentOccurrence_DateAlreadyIsTargetDay_ReturnsDateItself()
    {
        var friday = new DateOnly(2026, 7, 17);

        var result = NextRunTimeCalculator.GetMostRecentOccurrence(friday, DayOfWeek.Friday);

        Assert.Equal(friday, result);
    }

    [Fact]
    public void GetMostRecentOccurrence_DateIsBeforeTargetDayInCalendarWeek_GoesBackAFullWeek()
    {
        var sunday = new DateOnly(2026, 7, 19); // the day after Saturday 7/18 - Friday 7/17 already passed

        var result = NextRunTimeCalculator.GetMostRecentOccurrence(sunday, DayOfWeek.Friday);

        Assert.Equal(new DateOnly(2026, 7, 17), result);
    }
}
