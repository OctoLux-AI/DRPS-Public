using Drps.Execution.Candidates;
using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Execution.Candidates;

public class OpenCandidateQueryTests
{
    private static readonly DateTime Monday = new(2026, 7, 20, 10, 0, 0);
    // 2026-07-17 - the trading day immediately preceding Monday, used by the staleness-gate
    // worked example below (a Friday scan must still be fresh on Monday morning).
    private static readonly DateTime PrecedingFriday = new(2026, 7, 17, 16, 0, 0);

    // Default clock for every pre-existing test below: fixed to the exact ScanDate moment used
    // by BuildGateScore, so the new staleness gate (CLAUDE.md's Execution Layer: Seventh Design
    // Decision) never changes their existing behavior - 0 elapsed trading days is always fresh.
    private static OpenCandidateQuery BuildQuery(
        DrpsDbContext dbContext, DateTime? asOf = null, ILogger<OpenCandidateQuery>? logger = null) =>
        new(dbContext, logger ?? NullLogger<OpenCandidateQuery>.Instance, () => asOf ?? Monday);

    private static GateScore BuildGateScore(long id, string ticker, GateBucket bucket = GateBucket.Buy) => new()
    {
        Id = id,
        Ticker = ticker,
        ScanDate = Monday,
        Bucket = bucket,
        CompositeScore = 0.90m,
        DataAsOfDate = DateOnly.FromDateTime(Monday),
        CalculationVersion = 1,
        GateParameterVersion = 1
    };

    private static AdjusterAllocation BuildAllocation(long id, long gateScoreId) => new()
    {
        Id = id,
        GateScoreId = gateScoreId,
        AllocationDollarAmount = 1000m,
        AllocationPercent = 0.03m,
        ShareCount = 10m,
        ShareCapDeficient = false,
        AsOfTimestamp = Monday,
        AdjusterParameterVersion = 1,
        InsiderMultiplierApplied = 1.0m,
        InsiderDataUnverified = false
    };

    private static Position BuildOpenPosition(string ticker, long gateScoreId = 1, long adjusterAllocationId = 1) => new()
    {
        Ticker = ticker,
        GateScoreId = gateScoreId,
        AdjusterAllocationId = adjusterAllocationId,
        EntryDate = Monday.AddDays(-3),
        EntryPrice = 100m,
        EntryQuantity = 10m,
        ExitDate = null
    };

    private static ExcludedTicker BuildExcludedTicker(string ticker) => new()
    {
        Ticker = ticker,
        Reason = "Orphan Alpaca position with no Ledger record",
        CreatedDate = Monday.AddDays(-1)
    };

    [Fact]
    public async Task GetActionableBuyCandidatesAsync_BuyCandidateWithNoOpenPosition_IsReturned()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var gateScore = BuildGateScore(1, "AAPL");
        dbContext.GateScores.Add(gateScore);
        dbContext.AdjusterAllocations.Add(BuildAllocation(1, gateScore.Id));
        await dbContext.SaveChangesAsync();

        var query = BuildQuery(dbContext);
        var result = await query.GetActionableBuyCandidatesAsync(CancellationToken.None);

        var candidate = Assert.Single(result);
        Assert.Equal("AAPL", candidate.GateScore.Ticker);
        Assert.Equal(gateScore.Id, candidate.AdjusterAllocation.GateScoreId);
    }

    [Fact]
    public async Task GetActionableBuyCandidatesAsync_TickerAlreadyHasOpenPosition_IsExcluded()
    {
        // Per CLAUDE.md's Execution Layer: Sixth Design Decision - a currently-open Position
        // for this ticker is the entire idempotency check for opens, no new schema needed.
        using var dbContext = InMemoryDbContextFactory.Create();
        var gateScore = BuildGateScore(1, "AAPL");
        dbContext.GateScores.Add(gateScore);
        dbContext.AdjusterAllocations.Add(BuildAllocation(1, gateScore.Id));
        dbContext.Positions.Add(BuildOpenPosition("AAPL"));
        await dbContext.SaveChangesAsync();

        var query = BuildQuery(dbContext);
        var result = await query.GetActionableBuyCandidatesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetActionableBuyCandidatesAsync_TickerHasClosedPositionOnly_IsStillReturned()
    {
        // A closed position (ExitDate populated) must not be mistaken for "currently held" -
        // only ExitDate == null counts, same convention as LedgerPositionStateProvider.IsCurrentlyHeld.
        using var dbContext = InMemoryDbContextFactory.Create();
        var gateScore = BuildGateScore(1, "AAPL");
        dbContext.GateScores.Add(gateScore);
        dbContext.AdjusterAllocations.Add(BuildAllocation(1, gateScore.Id));
        var closedPosition = BuildOpenPosition("AAPL");
        closedPosition.ExitDate = Monday.AddDays(-1);
        closedPosition.ExitPrice = 105m;
        closedPosition.ExitQuantity = 10m;
        closedPosition.ExitReason = PositionExitReason.AtrStop;
        dbContext.Positions.Add(closedPosition);
        await dbContext.SaveChangesAsync();

        var query = BuildQuery(dbContext);
        var result = await query.GetActionableBuyCandidatesAsync(CancellationToken.None);

        Assert.Single(result);
    }

    [Theory]
    [InlineData(GateBucket.Watch)]
    [InlineData(GateBucket.Exit)]
    [InlineData(GateBucket.Neutral)]
    public async Task GetActionableBuyCandidatesAsync_NonBuyBucket_IsNeverReturned_RegardlessOfPositionState(GateBucket bucket)
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var gateScore = BuildGateScore(1, "AAPL", bucket);
        dbContext.GateScores.Add(gateScore);
        dbContext.AdjusterAllocations.Add(BuildAllocation(1, gateScore.Id));
        await dbContext.SaveChangesAsync();

        var query = BuildQuery(dbContext);
        var result = await query.GetActionableBuyCandidatesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetActionableBuyCandidatesAsync_BuyCandidateWithNoMatchingAdjusterAllocation_IsExcludedNotThrown()
    {
        // Per AdjusterScanService.DiscoverUnallocatedBuyCandidatesAsync's own documented
        // convention: an AdjusterAllocation row is only ever inserted for a candidate that was
        // actually Funded - NotFunded/Skipped/not-yet-sized BUY candidates never get one. That
        // is a normal, expected state, not an error, so this must be a silent exclusion (inner
        // join semantics), never an exception.
        using var dbContext = InMemoryDbContextFactory.Create();
        var gateScore = BuildGateScore(1, "AAPL");
        dbContext.GateScores.Add(gateScore);
        await dbContext.SaveChangesAsync();

        var query = BuildQuery(dbContext);
        var result = await query.GetActionableBuyCandidatesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetActionableBuyCandidatesAsync_BuyCandidateWithNoOpenPositionAndNoExcludedTickerEntry_IsReturned()
    {
        // Confirms existing behavior still holds with the ExcludedTicker check added alongside
        // the existing open-Position check.
        using var dbContext = InMemoryDbContextFactory.Create();
        var gateScore = BuildGateScore(1, "AAPL");
        dbContext.GateScores.Add(gateScore);
        dbContext.AdjusterAllocations.Add(BuildAllocation(1, gateScore.Id));
        await dbContext.SaveChangesAsync();

        var query = BuildQuery(dbContext);
        var result = await query.GetActionableBuyCandidatesAsync(CancellationToken.None);

        var candidate = Assert.Single(result);
        Assert.Equal("AAPL", candidate.GateScore.Ticker);
    }

    [Fact]
    public async Task GetActionableBuyCandidatesAsync_TickerPresentInExcludedTicker_IsExcluded()
    {
        // Per this session's orphan-exclusion decision - a ticker DRPS cannot auto-heal must
        // never be actionable, even though it's otherwise a perfectly valid BUY candidate with
        // no open Position.
        using var dbContext = InMemoryDbContextFactory.Create();
        var gateScore = BuildGateScore(1, "AAPL");
        dbContext.GateScores.Add(gateScore);
        dbContext.AdjusterAllocations.Add(BuildAllocation(1, gateScore.Id));
        dbContext.ExcludedTickers.Add(BuildExcludedTicker("AAPL"));
        await dbContext.SaveChangesAsync();

        var query = BuildQuery(dbContext);
        var result = await query.GetActionableBuyCandidatesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetActionableBuyCandidatesAsync_TickerHasOpenPositionAndExcludedTickerEntry_IsExcluded()
    {
        // Confirms the two exclusion conditions don't conflict - either reason alone is
        // sufficient, and having both simultaneously still results in exactly one exclusion,
        // not a query error or a double-counted result.
        using var dbContext = InMemoryDbContextFactory.Create();
        var gateScore = BuildGateScore(1, "AAPL");
        dbContext.GateScores.Add(gateScore);
        dbContext.AdjusterAllocations.Add(BuildAllocation(1, gateScore.Id));
        dbContext.Positions.Add(BuildOpenPosition("AAPL"));
        dbContext.ExcludedTickers.Add(BuildExcludedTicker("AAPL"));
        await dbContext.SaveChangesAsync();

        var query = BuildQuery(dbContext);
        var result = await query.GetActionableBuyCandidatesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetActionableBuyCandidatesAsync_ScanDateOnCurrentTradingDay_IsReturned()
    {
        // CLAUDE.md's Execution Layer: Seventh Design Decision - a scan from earlier the same
        // trading day (0 elapsed trading days) is always fresh.
        using var dbContext = InMemoryDbContextFactory.Create();
        var gateScore = BuildGateScore(1, "AAPL");
        dbContext.GateScores.Add(gateScore);
        dbContext.AdjusterAllocations.Add(BuildAllocation(1, gateScore.Id));
        await dbContext.SaveChangesAsync();

        var query = BuildQuery(dbContext, asOf: Monday.AddHours(2));
        var result = await query.GetActionableBuyCandidatesAsync(CancellationToken.None);

        Assert.Single(result);
    }

    [Fact]
    public async Task GetActionableBuyCandidatesAsync_FridayScanEvaluatedMondayMorning_IsStillFresh()
    {
        // The worked example from CLAUDE.md's Execution Layer: Seventh Design Decision - a
        // Friday ScanDate is exactly 1 elapsed trading day behind a Monday asOf (weekend
        // correctly absorbed by TradingDayCalendar's weekday-only counting), so it must still
        // count as fresh, not stale.
        using var dbContext = InMemoryDbContextFactory.Create();
        var gateScore = BuildGateScore(1, "AAPL");
        gateScore.ScanDate = PrecedingFriday;
        dbContext.GateScores.Add(gateScore);
        dbContext.AdjusterAllocations.Add(BuildAllocation(1, gateScore.Id));
        await dbContext.SaveChangesAsync();

        var query = BuildQuery(dbContext, asOf: Monday);
        var result = await query.GetActionableBuyCandidatesAsync(CancellationToken.None);

        Assert.Single(result);
    }

    [Fact]
    public async Task GetActionableBuyCandidatesAsync_ScanDateTwoTradingDaysStale_IsFilteredOut()
    {
        // A Thursday ScanDate is 2 elapsed trading days behind a Monday asOf (Friday + Monday) -
        // one trading day beyond the "current or immediately preceding trading day" gate, so it
        // must be excluded even though it is otherwise a perfectly valid, unheld BUY candidate.
        using var dbContext = InMemoryDbContextFactory.Create();
        var gateScore = BuildGateScore(1, "AAPL");
        gateScore.ScanDate = PrecedingFriday.AddDays(-1); // Thursday
        dbContext.GateScores.Add(gateScore);
        dbContext.AdjusterAllocations.Add(BuildAllocation(1, gateScore.Id));
        await dbContext.SaveChangesAsync();

        var query = BuildQuery(dbContext, asOf: Monday);
        var result = await query.GetActionableBuyCandidatesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetActionableBuyCandidatesAsync_StaleScanDate_DoesNotSuppressFreshCandidateSameCycle()
    {
        // Confirms the staleness filter is applied per-candidate, not as an all-or-nothing gate
        // on the whole result set.
        using var dbContext = InMemoryDbContextFactory.Create();
        var staleGateScore = BuildGateScore(1, "AAPL");
        staleGateScore.ScanDate = PrecedingFriday.AddDays(-1); // Thursday - stale
        var freshGateScore = BuildGateScore(2, "MSFT");
        freshGateScore.ScanDate = PrecedingFriday; // Friday - fresh on Monday
        dbContext.GateScores.AddRange(staleGateScore, freshGateScore);
        dbContext.AdjusterAllocations.Add(BuildAllocation(1, staleGateScore.Id));
        dbContext.AdjusterAllocations.Add(BuildAllocation(2, freshGateScore.Id));
        await dbContext.SaveChangesAsync();

        var query = BuildQuery(dbContext, asOf: Monday);
        var result = await query.GetActionableBuyCandidatesAsync(CancellationToken.None);

        var candidate = Assert.Single(result);
        Assert.Equal("MSFT", candidate.GateScore.Ticker);
    }

    // --- Per-ticker dedup (CLAUDE.md's Execution Layer: Eighth Design Decision) ---------------

    [Fact]
    public async Task GetActionableBuyCandidatesAsync_TwoBuyCandidatesSameTicker_OnlyLatestScanDateReturned()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var olderGateScore = BuildGateScore(1, "AAPL");
        olderGateScore.ScanDate = Monday;
        var newerGateScore = BuildGateScore(2, "AAPL");
        newerGateScore.ScanDate = Monday.AddHours(1);
        dbContext.GateScores.AddRange(olderGateScore, newerGateScore);
        dbContext.AdjusterAllocations.Add(BuildAllocation(1, olderGateScore.Id));
        dbContext.AdjusterAllocations.Add(BuildAllocation(2, newerGateScore.Id));
        await dbContext.SaveChangesAsync();

        var query = BuildQuery(dbContext, asOf: Monday.AddHours(2));
        var result = await query.GetActionableBuyCandidatesAsync(CancellationToken.None);

        var candidate = Assert.Single(result);
        Assert.Equal(newerGateScore.Id, candidate.GateScore.Id);
        Assert.Equal(newerGateScore.ScanDate, candidate.GateScore.ScanDate);
    }

    [Fact]
    public async Task GetActionableBuyCandidatesAsync_TieOnScanDate_HigherGateScoreIdWins()
    {
        // A tie on ScanDate itself breaks on the higher, auto-generated Id - the more recently
        // inserted row - rather than an arbitrary/unstable pick.
        using var dbContext = InMemoryDbContextFactory.Create();
        var firstInserted = BuildGateScore(1, "AAPL");
        firstInserted.ScanDate = Monday;
        var secondInserted = BuildGateScore(2, "AAPL");
        secondInserted.ScanDate = Monday; // exact same instant
        dbContext.GateScores.AddRange(firstInserted, secondInserted);
        dbContext.AdjusterAllocations.Add(BuildAllocation(1, firstInserted.Id));
        dbContext.AdjusterAllocations.Add(BuildAllocation(2, secondInserted.Id));
        await dbContext.SaveChangesAsync();

        var query = BuildQuery(dbContext, asOf: Monday);
        var result = await query.GetActionableBuyCandidatesAsync(CancellationToken.None);

        var candidate = Assert.Single(result);
        Assert.Equal(secondInserted.Id, candidate.GateScore.Id);
    }

    [Fact]
    public async Task GetActionableBuyCandidatesAsync_DuplicateTicker_DoesNotSuppressOtherTickerCandidateSameCycle()
    {
        // Confirms dedup is applied per-ticker, not as an all-or-nothing gate on the whole
        // result set - same shape as the equivalent staleness-filter isolation test above.
        using var dbContext = InMemoryDbContextFactory.Create();
        var olderAapl = BuildGateScore(1, "AAPL");
        olderAapl.ScanDate = Monday;
        var newerAapl = BuildGateScore(2, "AAPL");
        newerAapl.ScanDate = Monday.AddHours(1);
        var msft = BuildGateScore(3, "MSFT");
        msft.ScanDate = Monday;
        dbContext.GateScores.AddRange(olderAapl, newerAapl, msft);
        dbContext.AdjusterAllocations.Add(BuildAllocation(1, olderAapl.Id));
        dbContext.AdjusterAllocations.Add(BuildAllocation(2, newerAapl.Id));
        dbContext.AdjusterAllocations.Add(BuildAllocation(3, msft.Id));
        await dbContext.SaveChangesAsync();

        var query = BuildQuery(dbContext, asOf: Monday.AddHours(2));
        var result = await query.GetActionableBuyCandidatesAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, c => c.GateScore.Ticker == "MSFT");
        var aaplCandidate = Assert.Single(result, c => c.GateScore.Ticker == "AAPL");
        Assert.Equal(newerAapl.Id, aaplCandidate.GateScore.Id);
    }

    [Fact]
    public async Task GetActionableBuyCandidatesAsync_DedupDiscard_LogsSupersededDuplicateWithBothGateScoreIdsAndScanDates()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var olderGateScore = BuildGateScore(1, "AAPL");
        olderGateScore.ScanDate = Monday;
        var newerGateScore = BuildGateScore(2, "AAPL");
        newerGateScore.ScanDate = Monday.AddHours(1);
        dbContext.GateScores.AddRange(olderGateScore, newerGateScore);
        dbContext.AdjusterAllocations.Add(BuildAllocation(1, olderGateScore.Id));
        dbContext.AdjusterAllocations.Add(BuildAllocation(2, newerGateScore.Id));
        await dbContext.SaveChangesAsync();

        var capturingLogger = new CapturingLogger();
        var query = BuildQuery(dbContext, asOf: Monday.AddHours(2), logger: capturingLogger);
        await query.GetActionableBuyCandidatesAsync(CancellationToken.None);

        // The logger's structured-message formatter renders DateTime via invariant culture
        // (e.g. "07/20/2026 10:00:00"), not the current-culture default DateTime.ToString() -
        // matched here explicitly rather than assuming the two formats coincide.
        var olderScanDateFormatted = olderGateScore.ScanDate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var newerScanDateFormatted = newerGateScore.ScanDate.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Contains(capturingLogger.WarningMessages, m =>
            m.Contains("SUPERSEDED-DUPLICATE") && m.Contains("AAPL") &&
            m.Contains(olderGateScore.Id.ToString()) && m.Contains(newerGateScore.Id.ToString()) &&
            m.Contains(olderScanDateFormatted) && m.Contains(newerScanDateFormatted));
    }

    [Fact]
    public async Task GetActionableBuyCandidatesAsync_OlderDuplicateWouldAlsoBeStale_DiscardedAsDuplicateNotStaleness()
    {
        // Dedup runs before the staleness check (this task's explicit ordering requirement) -
        // an older duplicate that would independently fail the staleness gate must be logged as
        // a discarded duplicate, never as a spurious staleness warning.
        using var dbContext = InMemoryDbContextFactory.Create();
        var staleOlderGateScore = BuildGateScore(1, "AAPL");
        staleOlderGateScore.ScanDate = PrecedingFriday.AddDays(-1); // Thursday - stale on its own
        var freshNewerGateScore = BuildGateScore(2, "AAPL");
        freshNewerGateScore.ScanDate = Monday;
        dbContext.GateScores.AddRange(staleOlderGateScore, freshNewerGateScore);
        dbContext.AdjusterAllocations.Add(BuildAllocation(1, staleOlderGateScore.Id));
        dbContext.AdjusterAllocations.Add(BuildAllocation(2, freshNewerGateScore.Id));
        await dbContext.SaveChangesAsync();

        var capturingLogger = new CapturingLogger();
        var query = BuildQuery(dbContext, asOf: Monday, logger: capturingLogger);
        var result = await query.GetActionableBuyCandidatesAsync(CancellationToken.None);

        var candidate = Assert.Single(result);
        Assert.Equal(freshNewerGateScore.Id, candidate.GateScore.Id);

        Assert.Contains(capturingLogger.WarningMessages, m => m.Contains("SUPERSEDED-DUPLICATE"));
        Assert.DoesNotContain(capturingLogger.WarningMessages, m => m.Contains("STALENESS"));
    }

    private sealed class CapturingLogger : ILogger<OpenCandidateQuery>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<string> WarningMessages =>
            _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
