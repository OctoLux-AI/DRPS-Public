using Drps.Execution.PreFire;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Execution.PreFire;

public class KillSwitchTrackerTests
{
    // A confirmed Monday - avoids the non-weekday warning-log path in every test that doesn't
    // specifically exercise it.
    private static readonly DateTime Monday = new(2026, 7, 20, 10, 0, 0);

    [Fact]
    public async Task TryRecordOpenAttemptAsync_NoExistingRow_CreatesRowAndAllows()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new KillSwitchTracker(dbContext, NullLogger<KillSwitchTracker>.Instance);

        var allowed = await tracker.TryRecordOpenAttemptAsync(Monday, maxOpensPerDay: 5, CancellationToken.None);

        Assert.True(allowed);
        var counter = await dbContext.KillSwitchCounters.SingleAsync();
        Assert.Equal(DateOnly.FromDateTime(Monday), counter.TradingDate);
        Assert.Equal(1, counter.OpenAttemptCount);
    }

    [Fact]
    public async Task TryRecordOpenAttemptAsync_UnderThreshold_IncrementsAndAllows()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new KillSwitchTracker(dbContext, NullLogger<KillSwitchTracker>.Instance);

        await tracker.TryRecordOpenAttemptAsync(Monday, maxOpensPerDay: 5, CancellationToken.None);
        var allowed = await tracker.TryRecordOpenAttemptAsync(Monday, maxOpensPerDay: 5, CancellationToken.None);

        Assert.True(allowed);
        var counter = await dbContext.KillSwitchCounters.SingleAsync();
        Assert.Equal(2, counter.OpenAttemptCount);
    }

    [Fact]
    public async Task TryRecordOpenAttemptAsync_AtThreshold_TripsAndDoesNotIncrementFurther()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new KillSwitchTracker(dbContext, NullLogger<KillSwitchTracker>.Instance);

        // Two allowed attempts against a threshold of 2, then a third that must trip.
        await tracker.TryRecordOpenAttemptAsync(Monday, maxOpensPerDay: 2, CancellationToken.None);
        await tracker.TryRecordOpenAttemptAsync(Monday, maxOpensPerDay: 2, CancellationToken.None);
        var thirdAllowed = await tracker.TryRecordOpenAttemptAsync(Monday, maxOpensPerDay: 2, CancellationToken.None);

        Assert.False(thirdAllowed);
        var counter = await dbContext.KillSwitchCounters.SingleAsync();
        // Holds steady at the threshold rather than growing unbounded past it.
        Assert.Equal(2, counter.OpenAttemptCount);
    }

    [Fact]
    public async Task TryRecordOpenAttemptAsync_ZeroThreshold_TripsOnFirstAttempt()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new KillSwitchTracker(dbContext, NullLogger<KillSwitchTracker>.Instance);

        var allowed = await tracker.TryRecordOpenAttemptAsync(Monday, maxOpensPerDay: 0, CancellationToken.None);

        Assert.False(allowed);
    }

    [Fact]
    public async Task TryRecordOpenAttemptAsync_DifferentTradingDates_TrackedIndependently()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new KillSwitchTracker(dbContext, NullLogger<KillSwitchTracker>.Instance);
        var tuesday = Monday.AddDays(1);

        await tracker.TryRecordOpenAttemptAsync(Monday, maxOpensPerDay: 1, CancellationToken.None);
        // Monday is already at its threshold of 1, but Tuesday is a fresh trading day and must
        // not be blocked by Monday's count - confirms the reset boundary is real.
        var tuesdayAllowed = await tracker.TryRecordOpenAttemptAsync(tuesday, maxOpensPerDay: 1, CancellationToken.None);

        Assert.True(tuesdayAllowed);
        Assert.Equal(2, await dbContext.KillSwitchCounters.CountAsync());
    }

    // --- TryMarkNotifiedAsync -----------------------------------------------------------------

    [Fact]
    public async Task TryMarkNotifiedAsync_FirstCallForTradingDate_ReturnsTrueAndSetsNotifiedAt()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new KillSwitchTracker(dbContext, NullLogger<KillSwitchTracker>.Instance);
        await tracker.TryRecordOpenAttemptAsync(Monday, maxOpensPerDay: 0, CancellationToken.None);

        var shouldNotify = await tracker.TryMarkNotifiedAsync(Monday, CancellationToken.None);

        Assert.True(shouldNotify);
        var counter = await dbContext.KillSwitchCounters.SingleAsync();
        Assert.NotNull(counter.NotifiedAt);
    }

    [Fact]
    public async Task TryMarkNotifiedAsync_SecondCallSameTradingDate_ReturnsFalseAndLeavesNotifiedAtUnchanged()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new KillSwitchTracker(dbContext, NullLogger<KillSwitchTracker>.Instance);
        await tracker.TryRecordOpenAttemptAsync(Monday, maxOpensPerDay: 0, CancellationToken.None);

        await tracker.TryMarkNotifiedAsync(Monday, CancellationToken.None);
        var counterAfterFirst = await dbContext.KillSwitchCounters.SingleAsync();
        var notifiedAtAfterFirst = counterAfterFirst.NotifiedAt;

        var secondShouldNotify = await tracker.TryMarkNotifiedAsync(Monday, CancellationToken.None);

        Assert.False(secondShouldNotify);
        var counterAfterSecond = await dbContext.KillSwitchCounters.SingleAsync();
        Assert.Equal(notifiedAtAfterFirst, counterAfterSecond.NotifiedAt);
    }

    [Fact]
    public async Task TryMarkNotifiedAsync_DifferentTradingDates_TrackedIndependently()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new KillSwitchTracker(dbContext, NullLogger<KillSwitchTracker>.Instance);
        var tuesday = Monday.AddDays(1);
        await tracker.TryRecordOpenAttemptAsync(Monday, maxOpensPerDay: 0, CancellationToken.None);
        await tracker.TryRecordOpenAttemptAsync(tuesday, maxOpensPerDay: 0, CancellationToken.None);

        await tracker.TryMarkNotifiedAsync(Monday, CancellationToken.None);
        // Tuesday's own row must not already be marked notified just because Monday's was -
        // confirms the reset boundary applies to the notification flag too, not just the count.
        var tuesdayShouldNotify = await tracker.TryMarkNotifiedAsync(tuesday, CancellationToken.None);

        Assert.True(tuesdayShouldNotify);
    }

    [Fact]
    public async Task TryMarkNotifiedAsync_NoExistingCounterRow_ReturnsTrueWithoutThrowing()
    {
        // Defensive-only path: should never happen in practice (PreFireGateService only calls
        // this after TryRecordOpenAttemptAsync already created the row), but confirms the
        // fail-safe "proceed as not-yet-notified" behavior rather than a NullReferenceException.
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new KillSwitchTracker(dbContext, NullLogger<KillSwitchTracker>.Instance);

        var shouldNotify = await tracker.TryMarkNotifiedAsync(Monday, CancellationToken.None);

        Assert.True(shouldNotify);
        Assert.False(await dbContext.KillSwitchCounters.AnyAsync());
    }

    [Fact]
    public async Task TryRecordOpenAttemptAsync_WeekendDate_StillTracksNormallyWithoutThrowing()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new KillSwitchTracker(dbContext, NullLogger<KillSwitchTracker>.Instance);
        var saturday = new DateTime(2026, 7, 18, 10, 0, 0); // confirmed non-weekday relative to the Monday above

        var allowed = await tracker.TryRecordOpenAttemptAsync(saturday, maxOpensPerDay: 5, CancellationToken.None);

        // Not treated as an error here - the dedicated Market-Hours gate is what actually
        // rejects a non-trading-day fire attempt; this tracker just logs and proceeds.
        Assert.True(allowed);
    }
}
