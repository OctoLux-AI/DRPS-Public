using Drps.Execution;
using Drps.Execution.Alpaca;
using Drps.Execution.PreFire;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Execution.PreFire;

public class PreFireGateServiceTests
{
    // A confirmed Monday - avoids the non-weekday warning-log path in every test that isn't
    // specifically exercising it.
    private static readonly DateTime Monday = new(2026, 7, 20, 10, 0, 0);

    // Today's real shipped values (same fixture AdjusterSizingServiceTests/
    // AdjusterParametersValidatorTests use) - 25% base reserve, stepping to 35% at $10k and
    // 45% at $100k. MaxConcurrentPositions is left unset here (retains the model's own default
    // of 15) - every test in this file that seeds this fixture and has zero open Position rows
    // passes the concurrent-position-cap check trivially; tests exercising that gate directly
    // override MaxConcurrentPositions explicitly, per test.
    private static AdjusterParameters ActiveAdjusterParameters() => new()
    {
        IsActive = true,
        TierOneFloor = 0.85m,
        TierOneCeiling = 0.89m,
        TierTwoCeiling = 0.93m,
        TierOneBaseRate = 0.03m,
        TierTwoBaseRate = 0.04m,
        TierThreeBaseRate = 0.05m,
        SectorCapPercent = 0.30m,
        BaseReservePercent = 0.25m,
        ReserveStepPercent = 0.10m,
        ReserveMilestoneOne = 10000m,
        ReserveMilestoneTwo = 100000m
    };

    // Minimal valid open Position fixture (ExitDate null) - only the non-nullable columns are
    // set, matching what EvaluateConcurrentPositionCapAsync's COUNT query actually reads
    // (ExitDate only). GateScoreId/AdjusterAllocationId are arbitrary - the InMemory provider
    // doesn't enforce FK constraints, and this check has no reason to join against either table.
    private static Position OpenPosition(string ticker) => new()
    {
        Ticker = ticker,
        GateScoreId = 1,
        AdjusterAllocationId = 1,
        EntryDate = Monday,
        EntryPrice = 100m,
        EntryQuantity = 1m,
        OpenOrigin = PositionActionOrigin.Automated
    };

    private static PreFireGateService CreateService(
        FakeAlpacaTradingClient client, Drps.Ingestion.Persistence.DrpsDbContext dbContext, int maxOpensPerDay = 5, DateTime? asOf = null,
        FakePushoverNotificationService? pushoverNotificationService = null,
        IInFlightPositionTracker? inFlightPositionTracker = null)
    {
        var tracker = new KillSwitchTracker(dbContext, NullLogger<KillSwitchTracker>.Instance);
        var settings = Options.Create(new PreFireGateSettings { KillSwitchMaxOpensPerDay = maxOpensPerDay });
        var now = asOf ?? Monday;
        return new PreFireGateService(
            client, dbContext, tracker, settings, pushoverNotificationService ?? new FakePushoverNotificationService(),
            // Fresh per call unless the test explicitly shares one (needed to prove the
            // concurrent-position-cap reservation race-fix across multiple EvaluateOpenAsync
            // calls against different tickers/different PreFireGateService instances).
            inFlightPositionTracker ?? new InFlightPositionTracker(),
            NullLogger<PreFireGateService>.Instance, () => now);
    }

    private static PreFireOpenRequest Request(string symbol = "AAPL", decimal proposedDollarAmount = 1000m) =>
        new() { Symbol = symbol, ProposedDollarAmount = proposedDollarAmount };

    // --- Full pass ---------------------------------------------------------------------------

    [Fact]
    public async Task EvaluateOpenAsync_AllSixChecksPass_ReturnsPassed()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient();
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Null(result.FailedGate);
        Assert.Null(result.Reason);
        // Concurrent-position-cap (step 1) is a pure DB read - it never touches the Alpaca
        // client, so this sequence is unaffected by its addition ahead of market-hours.
        Assert.Equal(
            new[] { "GetClockAsync", "GetAccountAsync", "GetAssetAsync" },
            client.CalledMethods);
    }

    // --- Gate 1: Concurrent-position cap (2026-07-27, evaluated first of all six checks) -------

    [Fact]
    public async Task EvaluateOpenAsync_OpenPositionCountAtCap_FailsWithoutCallingAnyApi()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var parameters = ActiveAdjusterParameters();
        parameters.MaxConcurrentPositions = 3;
        dbContext.AdjusterParameters.Add(parameters);
        dbContext.Positions.AddRange(OpenPosition("AAA"), OpenPosition("BBB"), OpenPosition("CCC"));
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient();
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(PreFireGate.ConcurrentPositionCap, result.FailedGate);
        Assert.Contains("3/3", result.Reason);
        // Confirms the short-circuit: this is genuinely the first check - nothing else ran.
        Assert.Empty(client.CalledMethods);
    }

    [Fact]
    public async Task EvaluateOpenAsync_OpenPositionCountExceedsCap_Fails()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var parameters = ActiveAdjusterParameters();
        parameters.MaxConcurrentPositions = 3;
        dbContext.AdjusterParameters.Add(parameters);
        dbContext.Positions.AddRange(
            OpenPosition("AAA"), OpenPosition("BBB"), OpenPosition("CCC"), OpenPosition("DDD"));
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient();
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(PreFireGate.ConcurrentPositionCap, result.FailedGate);
        Assert.Contains("4/3", result.Reason);
    }

    [Fact]
    public async Task EvaluateOpenAsync_OpenPositionCountBelowCap_PassesThatCheck()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var parameters = ActiveAdjusterParameters();
        parameters.MaxConcurrentPositions = 3;
        dbContext.AdjusterParameters.Add(parameters);
        dbContext.Positions.AddRange(OpenPosition("AAA"), OpenPosition("BBB"));
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient();
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task EvaluateOpenAsync_ClosedPositionsNotCountedTowardCap_PassesThatCheck()
    {
        // A closed position (ExitDate set) must not count toward the cap - only genuinely open
        // rows do. Seeds MaxConcurrentPositions = 1 with one closed and zero open rows.
        using var dbContext = InMemoryDbContextFactory.Create();
        var parameters = ActiveAdjusterParameters();
        parameters.MaxConcurrentPositions = 1;
        dbContext.AdjusterParameters.Add(parameters);
        var closedPosition = OpenPosition("AAA");
        closedPosition.ExitDate = Monday;
        closedPosition.ExitPrice = 105m;
        closedPosition.ExitQuantity = 1m;
        closedPosition.ExitReason = PositionExitReason.AtrStop;
        closedPosition.CloseOrigin = PositionActionOrigin.Automated;
        dbContext.Positions.Add(closedPosition);
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient();
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task EvaluateOpenAsync_NoActiveAdjusterParameters_FailsAtConcurrentPositionCapFailClosed()
    {
        // Supersedes the pre-2026-07-27 version of this test, which expected FailedGate ==
        // CashFloor: absence of an active AdjusterParameters row is now caught by
        // concurrent-position-cap (step 1) before cash-floor (step 4 as of the 2026-08-02
        // reorder - was step 5) is ever reached. CashFloor's own identical fail-closed branch
        // (EvaluateCashFloorAsync's own "Count != 1" check) is consequently unreachable via
        // EvaluateOpenAsync today - a real, honestly-reported side effect of this reorder, not a
        // regression fixed in this task.
        using var dbContext = InMemoryDbContextFactory.Create();
        // Deliberately no AdjusterParameters row seeded.
        var client = new FakeAlpacaTradingClient();
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(PreFireGate.ConcurrentPositionCap, result.FailedGate);
        Assert.Contains("AdjusterParameters", result.Reason);
        Assert.Empty(client.CalledMethods);
    }

    [Fact]
    public async Task EvaluateOpenAsync_ConcurrentPositionCapAlwaysPrecedesCashFloor()
    {
        // Directly documents the ordering dependency EvaluateCashFloorAsync's own dead-branch
        // comment relies on (CLAUDE.md's "EvaluateCashFloorAsync's Unreachable Fail-Closed
        // Branch" addendum, 2026-07-28) - not merely an incidental consequence of the "no
        // active parameters" test above, but the specific invariant a future reorder must not
        // break silently. Concurrent-position-cap (step 1) shares the identical "exactly one
        // active AdjusterParameters row" fail-closed precondition with cash-floor (step 4 as of
        // the 2026-08-02 reorder - was step 5), and MUST be the one to catch it first: if a
        // future change ever moved cash-floor ahead of
        // concurrent-position-cap (or removed/weakened concurrent-position-cap's own check),
        // this test starts asserting PreFireGate.CashFloor here instead of
        // PreFireGate.ConcurrentPositionCap and fails loudly - the test-side mirror of the
        // safety net EvaluateCashFloorAsync's own kept-not-deleted dead branch provides on the
        // code side.
        using var dbContext = InMemoryDbContextFactory.Create();
        // Deliberately no AdjusterParameters row seeded - the one condition both checks share.
        var client = new FakeAlpacaTradingClient();
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.Equal(PreFireGate.ConcurrentPositionCap, result.FailedGate);
        Assert.NotEqual(PreFireGate.CashFloor, result.FailedGate);
        // Nothing past step 1 ran at all - proves execution never reached step 4 (cash-floor),
        // not just that the two gates' reasons happen to read differently.
        Assert.Empty(client.CalledMethods);
    }

    [Fact]
    public async Task EvaluateCloseAsync_AtOrAboveConcurrentPositionCap_StillPasses()
    {
        // The cap must never block a close - a close only ever reduces the open count.
        using var dbContext = InMemoryDbContextFactory.Create();
        var parameters = ActiveAdjusterParameters();
        parameters.MaxConcurrentPositions = 1;
        dbContext.AdjusterParameters.Add(parameters);
        dbContext.Positions.AddRange(OpenPosition("AAA"), OpenPosition("BBB"), OpenPosition("CCC"));
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient();
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateCloseAsync(CloseRequest(), CancellationToken.None);

        Assert.True(result.Passed);
        // Only the two close-side Alpaca calls ran, exactly as before this task -
        // EvaluateCloseAsync's own code (unchanged by this task) never calls
        // EvaluateConcurrentPositionCapAsync at all, so 3 open positions against a cap of 1 has
        // no effect on the outcome here.
        Assert.Equal(new[] { "GetClockAsync", "GetAssetAsync" }, client.CalledMethods);
    }

    [Fact]
    public async Task EvaluateOpenAsync_TwoConcurrentCandidatesSeeingSameStaleOpenCount_OnlyOnePasses()
    {
        // End-to-end proof of the same-day race fix (InFlightPositionTrackerTests covers the
        // tracker in isolation; this proves it through the real EvaluateOpenAsync path). Two
        // DIFFERENT tickers, sharing one PreFireGateService instance's tracker (as
        // OrchestrationWorker's real DI wiring does), both firing against a DB that genuinely
        // has 14 open positions - simulating the exact scenario where neither candidate's
        // Position row has been created yet because both are still mid-fire in the same cycle.
        // Without the reservation fix, both would independently see "14 < 15" and both pass.
        using var dbContext = InMemoryDbContextFactory.Create();
        var parameters = ActiveAdjusterParameters();
        parameters.MaxConcurrentPositions = 15;
        dbContext.AdjusterParameters.Add(parameters);
        dbContext.Positions.AddRange(Enumerable.Range(0, 14).Select(i => OpenPosition($"EXISTING{i}")));
        await dbContext.SaveChangesAsync();

        var sharedTracker = new InFlightPositionTracker();
        var firstClient = new FakeAlpacaTradingClient();
        var secondClient = new FakeAlpacaTradingClient();
        var firstService = CreateService(firstClient, dbContext, inFlightPositionTracker: sharedTracker);
        var secondService = CreateService(secondClient, dbContext, inFlightPositionTracker: sharedTracker);

        var firstResult = await firstService.EvaluateOpenAsync(Request("AAPL"), CancellationToken.None);
        var secondResult = await secondService.EvaluateOpenAsync(Request("MSFT"), CancellationToken.None);

        Assert.True(firstResult.Passed);
        Assert.False(secondResult.Passed);
        Assert.Equal(PreFireGate.ConcurrentPositionCap, secondResult.FailedGate);

        // Releasing the first candidate's reservation (as OrchestrationWorker's finally block
        // would once its fire+confirm cycle resolves) frees the slot for a third attempt.
        sharedTracker.Release("AAPL");
        var thirdResult = await secondService.EvaluateOpenAsync(Request("MSFT"), CancellationToken.None);
        Assert.True(thirdResult.Passed);
    }

    // --- Gate 6: Kill switch (evaluated LAST of all six checks, per the 2026-08-02 "Kill-Switch
    // Deferred to Last Check" reorder - closes the gap the 2026-07-27 reorder explicitly left
    // open, where kill switch ran third and could still consume a strike on an attempt circuit
    // breaker/cash floor/asset-tradable were always going to reject anyway) ----------------------

    [Fact]
    public async Task EvaluateOpenAsync_KillSwitchTripped_FailsOnlyAfterAllOtherChecksPass()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // Concurrent-position-cap (step 1) must pass so this test can actually exercise kill
        // switch (step 6, last) - zero open Position rows against the model's default cap of 15.
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient();
        var pushover = new FakePushoverNotificationService();
        var service = CreateService(client, dbContext, maxOpensPerDay: 0, pushoverNotificationService: pushover);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(PreFireGate.KillSwitch, result.FailedGate);
        Assert.NotNull(result.Reason);
        // 2026-08-02 reorder: kill switch is now step 6 (last) - every other check (market-hours,
        // circuit breaker, cash floor, asset-tradable) has already run and passed by the time
        // kill switch is even evaluated, confirmed here by every API call those checks make
        // having actually occurred, in order - not merely that the gate result says KillSwitch.
        Assert.Equal(new[] { "GetClockAsync", "GetAccountAsync", "GetAssetAsync" }, client.CalledMethods);

        // CLAUDE.md's Execution Layer: Ninth Design Decision - a kill-switch trip is one of
        // Pushover's wired trigger points.
        var notification = Assert.Single(pushover.SentMessages);
        Assert.Contains("kill switch", notification, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AAPL", notification);
    }

    [Fact]
    public async Task EvaluateOpenAsync_KillSwitchTrippedMultipleCalls_NotifiesOnceButFailsEveryCall()
    {
        // 2026-07-24 correction: the original notification fired unconditionally on every
        // EvaluateOpenAsync call while tripped - with OrchestrationSettings.PollIntervalSeconds
        // defaulting to 60, that produced one Pushover alert per minute for as long as the
        // tripped state persisted (confirmed live). This test locks in the fix: exactly one
        // notification for the whole trading day's trip, regardless of how many times the gate
        // is re-evaluated - while the rejection itself is unaffected on every single call.
        using var dbContext = InMemoryDbContextFactory.Create();
        // Concurrent-position-cap must pass so this test can reach kill switch.
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient();
        var pushover = new FakePushoverNotificationService();
        var service = CreateService(client, dbContext, maxOpensPerDay: 0, pushoverNotificationService: pushover);

        var first = await service.EvaluateOpenAsync(Request(), CancellationToken.None);
        var second = await service.EvaluateOpenAsync(Request(), CancellationToken.None);
        var third = await service.EvaluateOpenAsync(Request(), CancellationToken.None);
        var fourth = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        // Notification side-effect: exactly one, sent on the first call.
        var notification = Assert.Single(pushover.SentMessages);
        Assert.Contains("kill switch", notification, StringComparison.OrdinalIgnoreCase);

        // Gate outcome: unaffected by notification dedup - every call still fails identically.
        foreach (var result in new[] { first, second, third, fourth })
        {
            Assert.False(result.Passed);
            Assert.Equal(PreFireGate.KillSwitch, result.FailedGate);
            Assert.NotNull(result.Reason);
        }

        var counter = await dbContext.KillSwitchCounters.SingleAsync();
        Assert.NotNull(counter.NotifiedAt);
    }

    [Fact]
    public async Task EvaluateOpenAsync_KillSwitchAtThreshold_AllowsUpToLimitThenTripsOnNext()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient();
        var service = CreateService(client, dbContext, maxOpensPerDay: 2);

        var first = await service.EvaluateOpenAsync(Request(), CancellationToken.None);
        var second = await service.EvaluateOpenAsync(Request(), CancellationToken.None);
        var third = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.True(first.Passed);
        Assert.True(second.Passed);
        Assert.False(third.Passed);
        Assert.Equal(PreFireGate.KillSwitch, third.FailedGate);
    }

    // --- Gate 3: Consecutive-loss circuit breaker (moved ahead of kill switch by the 2026-08-02
    // reorder - was Gate 4, evaluated after kill switch, before that fix) ------------------------

    [Fact]
    public async Task EvaluateOpenAsync_CircuitBreakerTripped_FailsAfterMarketHoursOnly()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // Concurrent-position-cap and market-hours (steps 1 and 2) must both pass so this test
        // can reach circuit breaker (step 3).
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        dbContext.ConsecutiveLossCircuitBreakers.Add(new ConsecutiveLossCircuitBreaker
        {
            ConsecutiveLossCount = 3,
            Tripped = true,
            TrippedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient();
        var pushover = new FakePushoverNotificationService();
        var service = CreateService(client, dbContext, pushoverNotificationService: pushover);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(PreFireGate.ConsecutiveLossCircuitBreaker, result.FailedGate);
        Assert.Contains("3", result.Reason);
        // Concurrent-position-cap (step 1) is a pure DB read; market-hours (step 2) calls
        // GetClockAsync, so it's already been called - confirms the short-circuit for every
        // check AFTER circuit breaker (cash floor, asset-tradable, kill switch never run), not
        // zero API calls total.
        Assert.Equal(new[] { "GetClockAsync" }, client.CalledMethods);

        // CLAUDE.md's Execution Layer: Consecutive-Loss Circuit Breaker Design Decision, point 7 -
        // a fourth Pushover trigger, same shape as the kill switch's.
        var notification = Assert.Single(pushover.SentMessages);
        Assert.Contains("circuit breaker", notification, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AAPL", notification);
    }

    [Fact]
    public async Task EvaluateOpenAsync_CircuitBreakerTripped_DoesNotCreateKillSwitchCounterRow()
    {
        // The actual regression this reorder fixes for the circuit breaker specifically (mirrors
        // EvaluateOpenAsync_MarketClosed_DoesNotCreateKillSwitchCounterRow's own pattern): before
        // the 2026-08-02 reorder, kill switch ran at step 3, BEFORE circuit breaker's old step 4 -
        // so a tripped circuit breaker still consumed a kill-switch strike even though the
        // attempt was always going to be rejected one step later. With circuit breaker now
        // evaluated ahead of kill switch (step 6, last), a circuit-breaker rejection must never
        // touch KillSwitchCounters at all - kill switch is never reached.
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        dbContext.ConsecutiveLossCircuitBreakers.Add(new ConsecutiveLossCircuitBreaker
        {
            ConsecutiveLossCount = 3,
            Tripped = true,
            TrippedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient();
        var service = CreateService(client, dbContext, maxOpensPerDay: 5);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.Equal(PreFireGate.ConsecutiveLossCircuitBreaker, result.FailedGate);
        Assert.False(await dbContext.KillSwitchCounters.AnyAsync());
    }

    [Fact]
    public async Task EvaluateOpenAsync_CircuitBreakerTrippedMultipleCalls_NotifiesOnceButFailsEveryCall()
    {
        // Same dedup shape as the kill switch's own notification test - one alert per trip, not
        // one per re-evaluation, even though the reset boundary differs (manual only here, vs.
        // the kill switch's automatic per-trading-day reset).
        using var dbContext = InMemoryDbContextFactory.Create();
        // Concurrent-position-cap and market-hours must both pass so this test can reach circuit
        // breaker.
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        dbContext.ConsecutiveLossCircuitBreakers.Add(new ConsecutiveLossCircuitBreaker
        {
            ConsecutiveLossCount = 3,
            Tripped = true,
            TrippedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient();
        var pushover = new FakePushoverNotificationService();
        var service = CreateService(client, dbContext, pushoverNotificationService: pushover);

        var first = await service.EvaluateOpenAsync(Request(), CancellationToken.None);
        var second = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        var notification = Assert.Single(pushover.SentMessages);
        Assert.Contains("circuit breaker", notification, StringComparison.OrdinalIgnoreCase);

        foreach (var result in new[] { first, second })
        {
            Assert.False(result.Passed);
            Assert.Equal(PreFireGate.ConsecutiveLossCircuitBreaker, result.FailedGate);
        }

        var breaker = await dbContext.ConsecutiveLossCircuitBreakers.SingleAsync();
        Assert.NotNull(breaker.NotifiedAt);
    }

    [Fact]
    public async Task EvaluateOpenAsync_NoCircuitBreakerRowYet_PassesThatCheck()
    {
        // The common, healthy-pipeline state: no row exists until the first close is ever
        // recorded (LedgerPositionWriter.ClosePositionAsync creates it) - absence must not be
        // mistaken for a trip.
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient();
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task EvaluateOpenAsync_CircuitBreakerRowExistsButNotTripped_PassesThatCheck()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        dbContext.ConsecutiveLossCircuitBreakers.Add(new ConsecutiveLossCircuitBreaker { ConsecutiveLossCount = 1, Tripped = false });
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient();
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task EvaluateCloseAsync_CircuitBreakerTripped_StillPasses()
    {
        // Design decision point 3 - opens-only scope. A tripped circuit breaker must never
        // block a close, same reasoning as the kill switch's own exclusion from
        // EvaluateCloseAsync.
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.ConsecutiveLossCircuitBreakers.Add(new ConsecutiveLossCircuitBreaker
        {
            ConsecutiveLossCount = 5,
            Tripped = true,
            TrippedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient();
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateCloseAsync(CloseRequest(), CancellationToken.None);

        Assert.True(result.Passed);
    }

    // --- Gate 2: Market-hours (moved ahead of kill switch by the 2026-07-27 reorder; now second
    // overall behind the same day's concurrent-position-cap addition) ---------------------------

    [Fact]
    public async Task EvaluateOpenAsync_MarketClosed_FailsAtMarketHoursWithoutCallingAccountOrAsset()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // Concurrent-position-cap must pass so this test can reach market-hours.
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient
        {
            ClockResult = _ => Task.FromResult(new AlpacaClockResult { Success = true, IsOpen = false })
        };
        var pushover = new FakePushoverNotificationService();
        var service = CreateService(client, dbContext, pushoverNotificationService: pushover);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(PreFireGate.MarketHours, result.FailedGate);
        Assert.Equal(new[] { "GetClockAsync" }, client.CalledMethods);

        // Only a kill-switch trip is Pushover-notification-worthy (CLAUDE.md's Execution Layer:
        // Ninth Design Decision) - an ordinary market-hours rejection stays log-only.
        Assert.Empty(pushover.SentMessages);
    }

    [Fact]
    public async Task EvaluateOpenAsync_MarketClosed_DoesNotCreateKillSwitchCounterRow()
    {
        // The actual regression this reorder fixes, confirmed live 2026-07-27: before this task,
        // kill switch ran BEFORE market-hours, so an after-hours attempt still created/incremented
        // today's KillSwitchCounters row even though the attempt was always going to be rejected
        // one step later. With market-hours now evaluated ahead of kill switch, a market-closed
        // rejection must never touch KillSwitchCounters at all - kill switch is never reached.
        using var dbContext = InMemoryDbContextFactory.Create();
        // Concurrent-position-cap must pass so this test can reach market-hours.
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient
        {
            ClockResult = _ => Task.FromResult(new AlpacaClockResult { Success = true, IsOpen = false })
        };
        var service = CreateService(client, dbContext, maxOpensPerDay: 5);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(PreFireGate.MarketHours, result.FailedGate);
        Assert.False(await dbContext.KillSwitchCounters.AnyAsync());
    }

    [Fact]
    public async Task EvaluateOpenAsync_ClockFetchFails_FailsAtMarketHoursFailClosed()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // Concurrent-position-cap must pass so this test can reach market-hours.
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient
        {
            ClockResult = _ => Task.FromResult(new AlpacaClockResult { Success = false, ErrorMessage = "network error" })
        };
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(PreFireGate.MarketHours, result.FailedGate);
    }

    // --- Gate 4: Cash floor (moved ahead of kill switch by the 2026-08-02 reorder - was Gate 5)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task EvaluateOpenAsync_CashFloorBreached_FailsAtCashFloorWithoutCallingAsset()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await dbContext.SaveChangesAsync();

        // $50,000 buying power -> 35% tier (>= $10k milestone) -> floor = $17,500. An order
        // that leaves only $10,000 remaining breaches it.
        var client = new FakeAlpacaTradingClient
        {
            AccountResult = _ => Task.FromResult(new AlpacaAccountResult { Success = true, BuyingPower = 50000m, Cash = 50000m })
        };
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateOpenAsync(Request(proposedDollarAmount: 40000m), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(PreFireGate.CashFloor, result.FailedGate);
        Assert.Equal(new[] { "GetClockAsync", "GetAccountAsync" }, client.CalledMethods);
    }

    [Fact]
    public async Task EvaluateOpenAsync_CashFloorBreached_DoesNotCreateKillSwitchCounterRow()
    {
        // Mirrors EvaluateOpenAsync_MarketClosed_DoesNotCreateKillSwitchCounterRow's own pattern.
        // Before the 2026-08-02 reorder, kill switch ran at step 3, BEFORE cash floor's old step
        // 5 - so a breached cash floor still consumed a kill-switch strike even though the
        // attempt was always going to be rejected two steps later. With cash floor now evaluated
        // ahead of kill switch (step 6, last), a cash-floor rejection must never touch
        // KillSwitchCounters at all - kill switch is never reached.
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient
        {
            AccountResult = _ => Task.FromResult(new AlpacaAccountResult { Success = true, BuyingPower = 50000m, Cash = 50000m })
        };
        var service = CreateService(client, dbContext, maxOpensPerDay: 5);

        var result = await service.EvaluateOpenAsync(Request(proposedDollarAmount: 40000m), CancellationToken.None);

        Assert.Equal(PreFireGate.CashFloor, result.FailedGate);
        Assert.False(await dbContext.KillSwitchCounters.AnyAsync());
    }

    [Fact]
    public async Task EvaluateOpenAsync_CashFloorExactlyAtBoundary_Passes()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await dbContext.SaveChangesAsync();

        // $50,000 buying power -> floor = $17,500. An order leaving exactly $17,500 remaining
        // must pass (skip only on a genuine breach, not an exact match).
        var client = new FakeAlpacaTradingClient
        {
            AccountResult = _ => Task.FromResult(new AlpacaAccountResult { Success = true, BuyingPower = 50000m, Cash = 50000m })
        };
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateOpenAsync(Request(proposedDollarAmount: 32500m), CancellationToken.None);

        Assert.True(result.Passed);
    }

    // Note: "no active AdjusterParameters row" no longer manifests as a CashFloor rejection via
    // EvaluateOpenAsync - concurrent-position-cap (step 1) shares the identical fail-closed
    // "exactly one active row" requirement and now catches this condition first. See
    // EvaluateOpenAsync_NoActiveAdjusterParameters_FailsAtConcurrentPositionCapFailClosed in the
    // Gate 1 section above (this is a real, honestly-reported consequence of the 2026-07-27
    // reorder+cap addition, not something worked around here - EvaluateCashFloorAsync's own
    // identical fail-closed branch, still present in code, is simply unreachable via this path
    // now).

    [Fact]
    public async Task EvaluateOpenAsync_AccountFetchFails_FailsAtCashFloor()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient
        {
            AccountResult = _ => Task.FromResult(new AlpacaAccountResult { Success = false, ErrorMessage = "network error" })
        };
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(PreFireGate.CashFloor, result.FailedGate);
    }

    // --- Gate 5: Asset-tradable (moved ahead of kill switch by the 2026-08-02 reorder - was
    // Gate 6) ---------------------------------------------------------------------------------

    [Fact]
    public async Task EvaluateOpenAsync_AssetNotTradable_FailsAtAssetTradable()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient
        {
            AssetResult = (symbol, _) => Task.FromResult(new AlpacaAssetResult { Success = true, Symbol = symbol, Tradable = false, Status = "inactive" })
        };
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(PreFireGate.AssetTradable, result.FailedGate);
        Assert.Contains("inactive", result.Reason);
    }

    [Fact]
    public async Task EvaluateOpenAsync_AssetNotFound_FailsAtAssetTradableWithoutThrowing()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient
        {
            AssetResult = (symbol, _) => throw new SymbolNotFoundException(symbol)
        };
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateOpenAsync(Request("ZZZZINVALID"), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(PreFireGate.AssetTradable, result.FailedGate);
    }

    [Fact]
    public async Task EvaluateOpenAsync_AssetFetchFails_FailsAtAssetTradable()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient
        {
            AssetResult = (_, _) => Task.FromResult(new AlpacaAssetResult { Success = false, ErrorMessage = "network error" })
        };
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(PreFireGate.AssetTradable, result.FailedGate);
    }

    [Fact]
    public async Task EvaluateOpenAsync_AssetTradableFailure_DoesNotCreateKillSwitchCounterRow()
    {
        // Supersedes this test's own pre-2026-08-02 version (formerly named
        // "...IsNotACountedKillSwitchStrike"), which asserted counter.OpenAttemptCount == 1 as
        // CORRECT behavior - i.e. that an asset-tradable rejection still consumed a kill-switch
        // strike, because under the old order kill switch ran at step 3, BEFORE asset-tradable's
        // old step 6. That was exactly the wasted-strike bug the 2026-08-02 "Kill-Switch Deferred
        // to Last Check" reorder closes, mirroring EvaluateOpenAsync_MarketClosed_
        // DoesNotCreateKillSwitchCounterRow's own pattern. With asset-tradable now evaluated
        // ahead of kill switch (step 6, last), a rejection here must never touch
        // KillSwitchCounters at all - kill switch is never reached.
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient
        {
            AssetResult = (symbol, _) => Task.FromResult(new AlpacaAssetResult { Success = true, Symbol = symbol, Tradable = false, Status = "inactive" })
        };
        var service = CreateService(client, dbContext, maxOpensPerDay: 5);

        var result = await service.EvaluateOpenAsync(Request(), CancellationToken.None);

        Assert.Equal(PreFireGate.AssetTradable, result.FailedGate);
        Assert.False(await dbContext.KillSwitchCounters.AnyAsync());
    }

    // --- EvaluateCloseAsync: close-side counterpart, market-hours + asset-tradable only -------

    private static PreFireCloseRequest CloseRequest(string symbol = "AAPL") => new() { Symbol = symbol };

    [Fact]
    public async Task EvaluateCloseAsync_MarketHoursAndAssetTradablePass_ReturnsPassed()
    {
        // Deliberately no AdjusterParameters row seeded - EvaluateCloseAsync never reads it
        // (no cash-floor check), so its absence must not block a close the way it blocks an
        // open (EvaluateOpenAsync_NoActiveAdjusterParameters_FailsAtCashFloorFailClosed above).
        using var dbContext = InMemoryDbContextFactory.Create();
        var client = new FakeAlpacaTradingClient();
        var service = CreateService(client, dbContext, maxOpensPerDay: 0);

        var result = await service.EvaluateCloseAsync(CloseRequest(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Null(result.FailedGate);
        // Only the two close-side checks ran - confirms kill-switch and cash-floor (which
        // would call GetAccountAsync) were never touched, not merely that the counter stayed
        // at zero for some other reason.
        Assert.Equal(new[] { "GetClockAsync", "GetAssetAsync" }, client.CalledMethods);
    }

    [Fact]
    public async Task EvaluateCloseAsync_MarketClosed_FailsAtMarketHoursWithoutCallingAsset()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var client = new FakeAlpacaTradingClient
        {
            ClockResult = _ => Task.FromResult(new AlpacaClockResult { Success = true, IsOpen = false })
        };
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateCloseAsync(CloseRequest(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(PreFireGate.MarketHours, result.FailedGate);
        Assert.Equal(new[] { "GetClockAsync" }, client.CalledMethods);
    }

    [Fact]
    public async Task EvaluateCloseAsync_AssetNotTradable_FailsAtAssetTradable()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var client = new FakeAlpacaTradingClient
        {
            AssetResult = (symbol, _) => Task.FromResult(new AlpacaAssetResult { Success = true, Symbol = symbol, Tradable = false, Status = "halted" })
        };
        var service = CreateService(client, dbContext);

        var result = await service.EvaluateCloseAsync(CloseRequest(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(PreFireGate.AssetTradable, result.FailedGate);
        Assert.Contains("halted", result.Reason);
    }

    [Fact]
    public async Task EvaluateCloseAsync_NeverCallsAccountOrKillSwitch()
    {
        // Direct confirmation, not just an inference from Passed - GetAccountAsync (the
        // cash-floor check's own API call) must never appear in CalledMethods, and no
        // KillSwitchCounter row may ever be created, regardless of maxOpensPerDay.
        using var dbContext = InMemoryDbContextFactory.Create();
        var client = new FakeAlpacaTradingClient();
        var service = CreateService(client, dbContext, maxOpensPerDay: 0);

        await service.EvaluateCloseAsync(CloseRequest(), CancellationToken.None);

        Assert.DoesNotContain("GetAccountAsync", client.CalledMethods);
        Assert.False(await dbContext.KillSwitchCounters.AnyAsync());
    }
}
