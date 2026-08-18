using Drps.Execution;

namespace Drps.Tests;

// Unit tests for the concurrent-position-cap reservation mechanism (CLAUDE.md's same-day
// follow-up to "Adjuster: Concurrent-Position Cap Introduced" - the fix for the race where
// OrchestrationWorker fires multiple candidates from one cycle as independent, concurrent
// detached tasks, and PreFireGateService's own DB-count check alone cannot see each other's
// in-flight, not-yet-real Position rows). TryMarkInFlight/Release's existing per-ticker
// behavior (proven since this class's original introduction) is not re-tested here - this
// file is scoped to the new TryReserveOpenSlot mechanism specifically.
public class InFlightPositionTrackerTests
{
    [Fact]
    public void TryReserveOpenSlot_FifteenthReservationWithZeroOpenPositions_Succeeds()
    {
        // The exact scenario this task's own requirement describes: 15 positions already open
        // (simulated here as 14 already-reserved + 0 DB-open, i.e. the 15th reservation),
        // confirming the 15th candidate is allowed.
        var tracker = new InFlightPositionTracker();

        for (var i = 0; i < 14; i++)
        {
            Assert.True(tracker.TryReserveOpenSlot($"TICKER{i}", currentOpenPositionCount: 0, maxConcurrentPositions: 15));
        }

        Assert.True(tracker.TryReserveOpenSlot("TICKER14", currentOpenPositionCount: 0, maxConcurrentPositions: 15));
    }

    [Fact]
    public void TryReserveOpenSlot_SixteenthReservationWithFifteenAlreadyReserved_Fails()
    {
        // The 16th candidate, with 15 already reserved (0 DB-open + 15 in-flight reservations),
        // must be rejected - this task's own explicit requirement.
        var tracker = new InFlightPositionTracker();

        for (var i = 0; i < 15; i++)
        {
            Assert.True(tracker.TryReserveOpenSlot($"TICKER{i}", currentOpenPositionCount: 0, maxConcurrentPositions: 15));
        }

        Assert.False(tracker.TryReserveOpenSlot("TICKER15", currentOpenPositionCount: 0, maxConcurrentPositions: 15));
    }

    [Fact]
    public void TryReserveOpenSlot_CountsRealDbOpenPositionsAndReservationsTogether()
    {
        // 14 real open positions (DB) + this one reservation = 15, at the cap - allowed.
        // A subsequent 16th (still against the same currentOpenPositionCount=14, simulating two
        // candidates reading the same stale DB count before either's Position row exists) must
        // be rejected.
        var tracker = new InFlightPositionTracker();

        Assert.True(tracker.TryReserveOpenSlot("AAA", currentOpenPositionCount: 14, maxConcurrentPositions: 15));
        Assert.False(tracker.TryReserveOpenSlot("BBB", currentOpenPositionCount: 14, maxConcurrentPositions: 15));
    }

    [Fact]
    public void TryReserveOpenSlot_ThisIsTheActualRaceFix_TwoConcurrentCandidatesReadingSameStaleCount()
    {
        // The concrete race this fix closes: two DIFFERENT tickers' fire attempts, from the same
        // OrchestrationWorker cycle, each independently query the DB and both see 14 open
        // positions (neither's Position row exists yet - both are still mid-fire). Without this
        // reservation, both would pass a bare `14 >= 15` comparison and the cap would be
        // exceeded at 16. With it, only one of the two may proceed.
        var tracker = new InFlightPositionTracker();
        const int staleDbCountBothCandidatesSaw = 14;

        var firstReserved = tracker.TryReserveOpenSlot("AAPL", staleDbCountBothCandidatesSaw, maxConcurrentPositions: 15);
        var secondReserved = tracker.TryReserveOpenSlot("MSFT", staleDbCountBothCandidatesSaw, maxConcurrentPositions: 15);

        Assert.True(firstReserved);
        Assert.False(secondReserved);
    }

    [Fact]
    public void Release_FreesAReservedSlotForReuseByAnotherTicker()
    {
        var tracker = new InFlightPositionTracker();

        Assert.True(tracker.TryReserveOpenSlot("AAA", currentOpenPositionCount: 14, maxConcurrentPositions: 15));
        Assert.False(tracker.TryReserveOpenSlot("BBB", currentOpenPositionCount: 14, maxConcurrentPositions: 15));

        tracker.Release("AAA");

        // The freed slot is now available to a different ticker - Release must not be a
        // one-ticker-only, permanent removal from future eligibility.
        Assert.True(tracker.TryReserveOpenSlot("BBB", currentOpenPositionCount: 14, maxConcurrentPositions: 15));
    }

    [Fact]
    public void Release_OnATickerThatNeverReserved_IsANoOp()
    {
        // EvaluateCloseAsync never calls TryReserveOpenSlot at all, yet OrchestrationWorker's
        // finally block calls Release(ticker) for both opens and closes unconditionally - a
        // close-side release must not throw or corrupt state for a ticker with no reservation.
        var tracker = new InFlightPositionTracker();

        var exception = Record.Exception(() => tracker.Release("NEVER-RESERVED"));

        Assert.Null(exception);

        // Confirms no phantom reservation was created by the no-op release either.
        for (var i = 0; i < 15; i++)
        {
            Assert.True(tracker.TryReserveOpenSlot($"TICKER{i}", currentOpenPositionCount: 0, maxConcurrentPositions: 15));
        }
    }

    [Fact]
    public void TryReserveOpenSlot_SameTickerReservingTwiceWithoutRelease_DoesNotDoubleReserve()
    {
        // HashSet.Add's own idempotency (documented on TryReserveOpenSlot itself) - a ticker
        // already holding a reservation that somehow calls this again before releasing does not
        // consume a second slot.
        var tracker = new InFlightPositionTracker();

        Assert.True(tracker.TryReserveOpenSlot("AAA", currentOpenPositionCount: 0, maxConcurrentPositions: 2));
        Assert.True(tracker.TryReserveOpenSlot("AAA", currentOpenPositionCount: 0, maxConcurrentPositions: 2));

        // Cap is 2; AAA holds only one slot despite reserving twice, so a second, genuinely
        // different ticker must still be able to reserve the remaining slot.
        Assert.True(tracker.TryReserveOpenSlot("BBB", currentOpenPositionCount: 0, maxConcurrentPositions: 2));
    }
}
