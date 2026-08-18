using Drps.Ingestion.Persistence;
using Drps.Ledger;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;

namespace Drps.Tests.Ledger;

public class ManualPositionEntryServiceTests
{
    private static readonly DateTime EntryDate = new(2026, 7, 16, 9, 30, 0);
    private static readonly DateTime ExitDate = new(2026, 7, 18, 15, 45, 0);

    private static GateScore MakeGateScore(string ticker = "AAA") => new()
    {
        Ticker = ticker,
        Bucket = GateBucket.Buy,
        CompositeScore = 0.89m,
        ScanDate = EntryDate,
        CalculationVersion = 1,
        GateParameterVersion = 1
    };

    private static AdjusterAllocation MakeAdjusterAllocation(long gateScoreId) => new()
    {
        GateScoreId = gateScoreId,
        AllocationPercent = 0.03m,
        AllocationDollarAmount = 30000m,
        ShareCount = 300,
        ShareCapDeficient = false,
        AsOfTimestamp = EntryDate,
        AdjusterParameterVersion = 1
    };

    private static async Task<(long GateScoreId, long AdjusterAllocationId)> SeedValidForeignKeysAsync(DrpsDbContext dbContext)
    {
        var gateScore = MakeGateScore();
        dbContext.GateScores.Add(gateScore);
        await dbContext.SaveChangesAsync();

        var allocation = MakeAdjusterAllocation(gateScore.Id);
        dbContext.AdjusterAllocations.Add(allocation);
        await dbContext.SaveChangesAsync();

        return (gateScore.Id, allocation.Id);
    }

    [Fact]
    public async Task OpenPositionAsync_ValidForeignKeys_InsertsPositionWithNullExitAndLifecycleFields()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScoreId, allocationId) = await SeedValidForeignKeysAsync(dbContext);

        var service = new ManualPositionEntryService(dbContext);
        var position = await service.OpenPositionAsync(
            gateScoreId, allocationId, "AAA", EntryDate, 100.50m, 300m, CancellationToken.None);

        Assert.NotEqual(0, position.Id);
        Assert.Equal("AAA", position.Ticker);
        Assert.Equal(gateScoreId, position.GateScoreId);
        Assert.Equal(allocationId, position.AdjusterAllocationId);
        Assert.Equal(EntryDate, position.EntryDate);
        Assert.Equal(100.50m, position.EntryPrice);
        Assert.Equal(300m, position.EntryQuantity);
        Assert.Null(position.ExitDate);
        Assert.Null(position.ExitPrice);
        Assert.Null(position.ExitQuantity);
        Assert.Null(position.ExitReason);
        Assert.Null(position.LowGradeDate);
        Assert.Null(position.PlateauDate);
        Assert.Null(position.ReactivatedDate);
        Assert.Null(position.DeactivatedDate);
        Assert.Null(position.CoolDownStartDate);
        // Initial plateau baseline (2026-07-18 decision block, point 3) - entry is the only
        // reference point that exists before any reassessment has ever fired.
        Assert.Equal(100.50m, position.PlateauBaselinePrice);
        Assert.Equal(EntryDate, position.PlateauBaselineDate);

        var persisted = await dbContext.Positions.SingleAsync();
        Assert.Equal(position.Id, persisted.Id);
        Assert.Equal(100.50m, persisted.PlateauBaselinePrice);
        Assert.Equal(EntryDate, persisted.PlateauBaselineDate);
    }

    [Fact]
    public async Task OpenPositionAsync_NonexistentGateScoreId_ThrowsAndDoesNotInsert()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var (_, allocationId) = await SeedValidForeignKeysAsync(dbContext);

        var service = new ManualPositionEntryService(dbContext);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.OpenPositionAsync(999, allocationId, "AAA", EntryDate, 100m, 300m, CancellationToken.None));

        Assert.Empty(await dbContext.Positions.ToListAsync());
    }

    [Fact]
    public async Task OpenPositionAsync_NonexistentAdjusterAllocationId_ThrowsAndDoesNotInsert()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScoreId, _) = await SeedValidForeignKeysAsync(dbContext);

        var service = new ManualPositionEntryService(dbContext);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.OpenPositionAsync(gateScoreId, 999, "AAA", EntryDate, 100m, 300m, CancellationToken.None));

        Assert.Empty(await dbContext.Positions.ToListAsync());
    }

    [Fact]
    public async Task ClosePositionAsync_OpenPosition_SetsExitFieldsOnly()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScoreId, allocationId) = await SeedValidForeignKeysAsync(dbContext);

        var service = new ManualPositionEntryService(dbContext);
        var opened = await service.OpenPositionAsync(
            gateScoreId, allocationId, "AAA", EntryDate, 100m, 300m, CancellationToken.None);

        await service.ClosePositionAsync(
            opened.Id, ExitDate, 110m, 300m, PositionExitReason.AtrStop, CancellationToken.None);

        var closed = await dbContext.Positions.SingleAsync(p => p.Id == opened.Id);
        Assert.Equal(ExitDate, closed.ExitDate);
        Assert.Equal(110m, closed.ExitPrice);
        Assert.Equal(300m, closed.ExitQuantity);
        Assert.Equal(PositionExitReason.AtrStop, closed.ExitReason);

        // Untouched by ClosePositionAsync - only Gate/Adjuster's own narrow write carve-out
        // (or a never-happened re-scoring event) sets these.
        Assert.Null(closed.LowGradeDate);
        Assert.Null(closed.PlateauDate);
    }

    [Fact]
    public async Task ClosePositionAsync_AlreadyClosedPosition_Throws()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScoreId, allocationId) = await SeedValidForeignKeysAsync(dbContext);

        var service = new ManualPositionEntryService(dbContext);
        var opened = await service.OpenPositionAsync(
            gateScoreId, allocationId, "AAA", EntryDate, 100m, 300m, CancellationToken.None);
        await service.ClosePositionAsync(
            opened.Id, ExitDate, 110m, 300m, PositionExitReason.AtrStop, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ClosePositionAsync(
                opened.Id, ExitDate.AddDays(1), 120m, 300m, PositionExitReason.CompositeDegradation, CancellationToken.None));
    }

    [Fact]
    public async Task ClosePositionAsync_NonexistentPositionId_Throws()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var service = new ManualPositionEntryService(dbContext);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ClosePositionAsync(
                999, ExitDate, 110m, 300m, PositionExitReason.AtrStop, CancellationToken.None));
    }

    // --- Hardening: positive-value validation ------------------------------------------------
    // Added when LedgerPositionWriter was hardened against three gaps noted at extraction time:
    // no positive-value validation, a genuine concurrent-close race, and no write-layer guard
    // against opening a second position for an already-open ticker.

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task OpenPositionAsync_NonPositiveEntryPrice_ThrowsInvalidPositionValueExceptionAndDoesNotInsert(decimal entryPrice)
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScoreId, allocationId) = await SeedValidForeignKeysAsync(dbContext);

        var service = new ManualPositionEntryService(dbContext);

        var ex = await Assert.ThrowsAsync<InvalidPositionValueException>(() =>
            service.OpenPositionAsync(gateScoreId, allocationId, "AAA", EntryDate, entryPrice, 300m, CancellationToken.None));

        Assert.Equal("entryPrice", ex.FieldName);
        Assert.Equal(entryPrice, ex.Value);
        Assert.Empty(await dbContext.Positions.ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task OpenPositionAsync_NonPositiveEntryQuantity_ThrowsInvalidPositionValueExceptionAndDoesNotInsert(decimal entryQuantity)
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScoreId, allocationId) = await SeedValidForeignKeysAsync(dbContext);

        var service = new ManualPositionEntryService(dbContext);

        var ex = await Assert.ThrowsAsync<InvalidPositionValueException>(() =>
            service.OpenPositionAsync(gateScoreId, allocationId, "AAA", EntryDate, 100m, entryQuantity, CancellationToken.None));

        Assert.Equal("entryQuantity", ex.FieldName);
        Assert.Equal(entryQuantity, ex.Value);
        Assert.Empty(await dbContext.Positions.ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ClosePositionAsync_NonPositiveExitPrice_ThrowsInvalidPositionValueExceptionAndDoesNotClose(decimal exitPrice)
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScoreId, allocationId) = await SeedValidForeignKeysAsync(dbContext);

        var service = new ManualPositionEntryService(dbContext);
        var opened = await service.OpenPositionAsync(
            gateScoreId, allocationId, "AAA", EntryDate, 100m, 300m, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidPositionValueException>(() =>
            service.ClosePositionAsync(opened.Id, ExitDate, exitPrice, 300m, PositionExitReason.AtrStop, CancellationToken.None));

        Assert.Equal("exitPrice", ex.FieldName);
        Assert.Equal(exitPrice, ex.Value);

        // Refused before the position row was ever touched - still open, untouched.
        var stillOpen = await dbContext.Positions.SingleAsync(p => p.Id == opened.Id);
        Assert.Null(stillOpen.ExitDate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ClosePositionAsync_NonPositiveExitQuantity_ThrowsInvalidPositionValueExceptionAndDoesNotClose(decimal exitQuantity)
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScoreId, allocationId) = await SeedValidForeignKeysAsync(dbContext);

        var service = new ManualPositionEntryService(dbContext);
        var opened = await service.OpenPositionAsync(
            gateScoreId, allocationId, "AAA", EntryDate, 100m, 300m, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidPositionValueException>(() =>
            service.ClosePositionAsync(opened.Id, ExitDate, 110m, exitQuantity, PositionExitReason.AtrStop, CancellationToken.None));

        Assert.Equal("exitQuantity", ex.FieldName);
        Assert.Equal(exitQuantity, ex.Value);

        var stillOpen = await dbContext.Positions.SingleAsync(p => p.Id == opened.Id);
        Assert.Null(stillOpen.ExitDate);
    }

    // --- Hardening: concurrent-close race -----------------------------------------------------

    [Fact]
    public async Task ClosePositionAsync_ConcurrentModification_ThrowsPositionAlreadyModifiedExceptionNotRawEfException()
    {
        // EF Core's InMemory provider does not auto-generate a new RowVersion value on update
        // (confirmed empirically - a plain insert leaves RowVersion null, and it stays null
        // through further updates), so a natural two-DbContext race can't be reproduced by
        // simply having two contexts load-then-save in sequence: neither side's RowVersion ever
        // actually changes, so no mismatch is ever detected. Real SQL Server's ROWVERSION column
        // does regenerate on every update - this is purely a test-provider limitation.
        //
        // Instead, this test directly forces the tracked ORIGINAL RowVersion value to something
        // that cannot match whatever is actually stored - simulating "another writer changed
        // this row since we loaded it" without depending on real rowversion generation. This is
        // a genuine EF Core mechanism (confirmed empirically): SaveChangesAsync compares a
        // concurrency-token property's tracked original value against the current store value,
        // and does throw DbUpdateConcurrencyException on a real mismatch under InMemory.
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScoreId, allocationId) = await SeedValidForeignKeysAsync(dbContext);

        var service = new ManualPositionEntryService(dbContext);
        var opened = await service.OpenPositionAsync(
            gateScoreId, allocationId, "AAA", EntryDate, 100m, 300m, CancellationToken.None);

        var writer = new LedgerPositionWriter(dbContext);

        // Load the position into the change tracker (ClosePositionAsync will resolve to this
        // same tracked instance, not issue a second, independent load) so its OriginalValue can
        // be forced before the close attempt.
        var tracked = await dbContext.Positions.FirstAsync(p => p.Id == opened.Id);
        dbContext.Entry(tracked).Property(p => p.RowVersion).OriginalValue = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        var ex = await Assert.ThrowsAsync<PositionAlreadyModifiedException>(() =>
            writer.ClosePositionAsync(opened.Id, ExitDate, 110m, 300m, PositionExitReason.AtrStop, CancellationToken.None, PositionActionOrigin.Manual));

        Assert.Equal(opened.Id, ex.PositionId);
        Assert.IsType<DbUpdateConcurrencyException>(ex.InnerException);

        // The conflicting save never landed - position is still open.
        var stillOpen = await dbContext.Positions.AsNoTracking().SingleAsync(p => p.Id == opened.Id);
        Assert.Null(stillOpen.ExitDate);
    }

    // --- Hardening: duplicate-open guard -------------------------------------------------------
    //
    // EF Core's InMemory provider does not enforce unique indexes at all - filtered OR plain/
    // unfiltered - confirmed empirically via throwaway diagnostics before writing this test (a
    // second insert with a duplicate Ticker never throws, with or without HasFilter). This is a
    // real, confirmed gap in the test provider, not an assumption: the filtered unique index
    // itself (Ticker, WHERE ExitDate IS NULL) is only verifiable against a real SQL Server
    // database - the generated migration's SQL (CreateIndex ... unique: true, filter: "[ExitDate]
    // IS NULL") is correct T-SQL, confirmed by direct inspection of the migration file.
    //
    // Given that, this test exercises LedgerPositionWriter's OWN exception-translation logic
    // deterministically via its injectable SaveChangesAsync seam, rather than relying on a real
    // constraint violation that this test suite's provider cannot produce. It verifies "when
    // SaveChangesAsync throws DbUpdateException, OpenPositionAsync rethrows
    // DuplicateOpenPositionException wrapping it" - the actual behavior this class's code
    // guarantees - not that the database constraint itself fires (that half is verified by
    // reading the migration's generated SQL, not by an automated test).

    [Fact]
    public async Task OpenPositionAsync_SaveChangesThrowsDbUpdateException_ThrowsDuplicateOpenPositionException()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScoreId, allocationId) = await SeedValidForeignKeysAsync(dbContext);

        var simulatedConstraintViolation = new DbUpdateException(
            "Cannot insert duplicate key row in object 'dbo.Positions' with unique index 'IX_Positions_Ticker'.",
            new Exception("unique constraint violation"));

        var writer = new LedgerPositionWriter(dbContext, saveChangesAsync: _ => throw simulatedConstraintViolation);

        var ex = await Assert.ThrowsAsync<DuplicateOpenPositionException>(() =>
            writer.OpenPositionAsync(gateScoreId, allocationId, "AAA", EntryDate, 100m, 300m, CancellationToken.None, PositionActionOrigin.Manual));

        Assert.Equal("AAA", ex.Ticker);
        Assert.Same(simulatedConstraintViolation, ex.InnerException);

        // The simulated failure means nothing was ever actually persisted - confirms the
        // translation happens without any additional, unexpected side effect.
        Assert.Empty(await dbContext.Positions.ToListAsync());
    }

    [Fact]
    public async Task OpenPositionAsync_DefaultConstructor_UsesRealSaveChangesAndStillInsertsSuccessfully()
    {
        // Confirms the injectable seam's default (no saveChangesAsync argument supplied) is a
        // pure pass-through to the real DbContext.SaveChangesAsync - the same production
        // behavior as before this seam was added, exercised here via LedgerPositionWriter
        // directly rather than through ManualPositionEntryService.
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScoreId, allocationId) = await SeedValidForeignKeysAsync(dbContext);

        var writer = new LedgerPositionWriter(dbContext);
        var position = await writer.OpenPositionAsync(gateScoreId, allocationId, "AAA", EntryDate, 100m, 300m, CancellationToken.None, PositionActionOrigin.Manual);

        Assert.NotEqual(0, position.Id);
        Assert.Single(await dbContext.Positions.ToListAsync());
    }

    // --- Consecutive-loss circuit breaker (CLAUDE.md's Execution Layer: Consecutive-Loss
    // Circuit Breaker Design Decision, 2026-07-26, as refined by the Tenth Design Decision,
    // 2026-07-26 later) - the bookkeeping lives inside ClosePositionAsync itself (point 5), but
    // is now filtered to CloseOrigin == Automated only (Tenth Design Decision, point 5/6). These
    // core-mechanics tests exercise LedgerPositionWriter directly with
    // PositionActionOrigin.Automated - manual closes (ManualPositionEntryService) no longer
    // touch the breaker at all, verified separately in its own section below. Threshold is 3
    // (point 2, an unvalidated placeholder).

    private async Task<long> OpenAndCloseAsync(
        DrpsDbContext dbContext, LedgerPositionWriter writer, decimal entryPrice, decimal exitPrice, string ticker = "AAA")
    {
        var (gateScoreId, allocationId) = await SeedValidForeignKeysAsync(dbContext);
        var opened = await writer.OpenPositionAsync(
            gateScoreId, allocationId, ticker, EntryDate, entryPrice, 300m, CancellationToken.None, PositionActionOrigin.Automated);
        await writer.ClosePositionAsync(
            opened.Id, ExitDate, exitPrice, 300m, PositionExitReason.AtrStop, CancellationToken.None, PositionActionOrigin.Automated);
        return opened.Id;
    }

    [Fact]
    public async Task ClosePositionAsync_SingleLosingClose_IncrementsCountAndDoesNotTrip()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var writer = new LedgerPositionWriter(dbContext);

        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 90m);

        var breaker = await dbContext.ConsecutiveLossCircuitBreakers.SingleAsync();
        Assert.Equal(1, breaker.ConsecutiveLossCount);
        Assert.False(breaker.Tripped);
        Assert.Null(breaker.TrippedAt);
    }

    [Fact]
    public async Task ClosePositionAsync_WinningClose_DoesNotIncrementCount()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var writer = new LedgerPositionWriter(dbContext);

        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 110m);

        var breaker = await dbContext.ConsecutiveLossCircuitBreakers.SingleAsync();
        Assert.Equal(0, breaker.ConsecutiveLossCount);
        Assert.False(breaker.Tripped);
    }

    [Fact]
    public async Task ClosePositionAsync_BreakevenClose_IsNotALossAndResetsCount()
    {
        // ExitPrice == EntryPrice is breakeven, not a loss (design decision point 1 - strict
        // less-than only).
        using var dbContext = InMemoryDbContextFactory.Create();
        var writer = new LedgerPositionWriter(dbContext);

        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 90m, ticker: "AAA");
        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 100m, ticker: "BBB");

        var breaker = await dbContext.ConsecutiveLossCircuitBreakers.SingleAsync();
        Assert.Equal(0, breaker.ConsecutiveLossCount);
    }

    [Fact]
    public async Task ClosePositionAsync_WinBreaksAStreak_ResetsCountToZeroWithoutClearingAnyPriorTrip()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var writer = new LedgerPositionWriter(dbContext);

        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 90m, ticker: "AAA");
        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 90m, ticker: "BBB");
        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 110m, ticker: "CCC");

        var breaker = await dbContext.ConsecutiveLossCircuitBreakers.SingleAsync();
        Assert.Equal(0, breaker.ConsecutiveLossCount);
        Assert.False(breaker.Tripped);
    }

    [Fact]
    public async Task ClosePositionAsync_ThreeConsecutiveLosses_Trips()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var writer = new LedgerPositionWriter(dbContext);

        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 90m, ticker: "AAA");
        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 90m, ticker: "BBB");
        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 90m, ticker: "CCC");

        var breaker = await dbContext.ConsecutiveLossCircuitBreakers.SingleAsync();
        Assert.Equal(3, breaker.ConsecutiveLossCount);
        Assert.True(breaker.Tripped);
        Assert.NotNull(breaker.TrippedAt);
    }

    [Fact]
    public async Task ClosePositionAsync_AcrossDifferentTickers_CountsAccountWideNotPerTicker()
    {
        // Design decision point 1 - "consecutive" is account-wide, ordered by ExitDate,
        // explicitly not per-ticker (that's the NoBuy list's job instead).
        using var dbContext = InMemoryDbContextFactory.Create();
        var writer = new LedgerPositionWriter(dbContext);

        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 90m, ticker: "AAA");
        await OpenAndCloseAsync(dbContext, writer, entryPrice: 50m, exitPrice: 40m, ticker: "BBB");
        await OpenAndCloseAsync(dbContext, writer, entryPrice: 25m, exitPrice: 20m, ticker: "CCC");

        Assert.Single(await dbContext.ConsecutiveLossCircuitBreakers.ToListAsync());
        var breaker = await dbContext.ConsecutiveLossCircuitBreakers.SingleAsync();
        Assert.Equal(3, breaker.ConsecutiveLossCount);
        Assert.True(breaker.Tripped);
    }

    [Fact]
    public async Task ClosePositionAsync_LossesAfterAlreadyTripped_PreservesOriginalTrippedAt()
    {
        // TrippedAt is set once, the first time the threshold is crossed - opens are blocked
        // once tripped, but closes are not (scope is opens-only, point 3), so further real
        // losing closes on already-open positions remain possible while tripped.
        using var dbContext = InMemoryDbContextFactory.Create();
        var writer = new LedgerPositionWriter(dbContext);

        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 90m, ticker: "AAA");
        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 90m, ticker: "BBB");
        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 90m, ticker: "CCC");

        var afterTrip = await dbContext.ConsecutiveLossCircuitBreakers.SingleAsync();
        var firstTrippedAt = afterTrip.TrippedAt;
        Assert.NotNull(firstTrippedAt);

        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 90m, ticker: "DDD");

        var afterFourth = await dbContext.ConsecutiveLossCircuitBreakers.SingleAsync();
        Assert.Equal(4, afterFourth.ConsecutiveLossCount);
        Assert.True(afterFourth.Tripped);
        Assert.Equal(firstTrippedAt, afterFourth.TrippedAt);
    }

    [Fact]
    public async Task ClosePositionAsync_WinAfterTrip_ResetsCountButDoesNotClearTripped()
    {
        // Manual-reset-only (point 4) - a subsequent win does not auto-clear a trip. Only the
        // reset-circuit-breaker CLI action does.
        using var dbContext = InMemoryDbContextFactory.Create();
        var writer = new LedgerPositionWriter(dbContext);

        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 90m, ticker: "AAA");
        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 90m, ticker: "BBB");
        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 90m, ticker: "CCC");
        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 110m, ticker: "DDD");

        var breaker = await dbContext.ConsecutiveLossCircuitBreakers.SingleAsync();
        Assert.Equal(0, breaker.ConsecutiveLossCount);
        Assert.True(breaker.Tripped);
    }

    [Fact]
    public async Task ClosePositionAsync_ExitReasonReconciliationHealed_CountsIdenticallyToOtherExitReasons()
    {
        // Point 1 - every PositionExitReason counts identically; the breaker cares about the
        // realized outcome, not the mechanism that produced the close.
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScoreId, allocationId) = await SeedValidForeignKeysAsync(dbContext);
        var writer = new LedgerPositionWriter(dbContext);

        var opened = await writer.OpenPositionAsync(
            gateScoreId, allocationId, "AAA", EntryDate, 100m, 300m, CancellationToken.None, PositionActionOrigin.Automated);
        await writer.ClosePositionAsync(
            opened.Id, ExitDate, 90m, 300m, PositionExitReason.ReconciliationHealed, CancellationToken.None, PositionActionOrigin.Automated);

        var breaker = await dbContext.ConsecutiveLossCircuitBreakers.SingleAsync();
        Assert.Equal(1, breaker.ConsecutiveLossCount);
    }

    [Fact]
    public async Task ClosePositionAsync_LastEvaluatedPositionIdAlreadyMatches_DoesNotDoubleCount()
    {
        // Point 6's named double-counting guard. Not reachable through the public API in
        // practice (ClosePositionAsync already refuses to close an already-closed Position), so
        // this test forces the scenario directly by pre-seeding the guard field before the one
        // real close call - confirming the guard itself works, not that it's reachable live.
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScoreId, allocationId) = await SeedValidForeignKeysAsync(dbContext);
        var writer = new LedgerPositionWriter(dbContext);

        var opened = await writer.OpenPositionAsync(
            gateScoreId, allocationId, "AAA", EntryDate, 100m, 300m, CancellationToken.None, PositionActionOrigin.Automated);

        dbContext.ConsecutiveLossCircuitBreakers.Add(new ConsecutiveLossCircuitBreaker
        {
            LastEvaluatedPositionId = opened.Id,
            ConsecutiveLossCount = 2
        });
        await dbContext.SaveChangesAsync();

        await writer.ClosePositionAsync(
            opened.Id, ExitDate, 90m, 300m, PositionExitReason.AtrStop, CancellationToken.None, PositionActionOrigin.Automated);

        var breaker = await dbContext.ConsecutiveLossCircuitBreakers.SingleAsync();
        Assert.Equal(2, breaker.ConsecutiveLossCount);
    }

    // --- Circuit breaker origin-scoping (CLAUDE.md's Execution Layer: Tenth Design Decision) -
    // a manual close (ManualPositionEntryService, the real production manual-CLI path) must
    // neither increment, reset, nor otherwise touch ConsecutiveLossCircuitBreaker in either
    // direction, regardless of win/loss. Distinct from the tests above, which all use
    // LedgerPositionWriter directly with PositionActionOrigin.Automated to verify the breaker's
    // core mechanics still work correctly for the path that IS supposed to drive it.

    [Fact]
    public async Task ClosePositionAsync_ManualLoss_DoesNotTouchCircuitBreakerAtAll()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var service = new ManualPositionEntryService(dbContext);

        var (gateScoreId, allocationId) = await SeedValidForeignKeysAsync(dbContext);
        var opened = await service.OpenPositionAsync(
            gateScoreId, allocationId, "AAA", EntryDate, 100m, 300m, CancellationToken.None);
        await service.ClosePositionAsync(
            opened.Id, ExitDate, 90m, 300m, PositionExitReason.AtrStop, CancellationToken.None);

        // No breaker row is ever created - UpdateCircuitBreakerAsync returns before even
        // find-or-creating one, for a non-Automated CloseOrigin.
        Assert.Empty(await dbContext.ConsecutiveLossCircuitBreakers.ToListAsync());
    }

    [Fact]
    public async Task ClosePositionAsync_ManualWinAfterAutomatedLosses_DoesNotResetActiveStreak()
    {
        // Symmetric with the loss case above - a manual close must not touch the breaker in
        // EITHER direction. Two real automated losses (not yet tripped) followed by one manual
        // win must leave the count at 2, not reset to 0.
        using var dbContext = InMemoryDbContextFactory.Create();
        var writer = new LedgerPositionWriter(dbContext);
        var service = new ManualPositionEntryService(dbContext);

        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 90m, ticker: "AAA");
        await OpenAndCloseAsync(dbContext, writer, entryPrice: 100m, exitPrice: 90m, ticker: "BBB");

        var (gateScoreId, allocationId) = await SeedValidForeignKeysAsync(dbContext);
        var manuallyOpened = await service.OpenPositionAsync(
            gateScoreId, allocationId, "CCC", EntryDate, 100m, 300m, CancellationToken.None);
        await service.ClosePositionAsync(
            manuallyOpened.Id, ExitDate, 110m, 300m, PositionExitReason.AtrStop, CancellationToken.None);

        var breaker = await dbContext.ConsecutiveLossCircuitBreakers.SingleAsync();
        Assert.Equal(2, breaker.ConsecutiveLossCount);
        Assert.False(breaker.Tripped);
    }
}
