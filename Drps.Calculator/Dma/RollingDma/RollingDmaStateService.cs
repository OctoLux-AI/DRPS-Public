using Drps.Calculator.Calendar;
using Drps.Calculator.Indicators;
using Drps.Calculator.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Calculator.Dma.RollingDma;

/// <summary>
/// Nightly full-universe DMA rolling-state update - CLAUDE.md's "Rolling DMA State Machine"
/// (2026-07-22). Reads that night's <see cref="UniverseSnapshot"/> in full, exactly like
/// <see cref="Drps.Ingestion.Orchestration.UniverseBarSweepRunner"/> - no DMA pre-screen, no
/// watchlist, no eligibility gate of any kind (CLAUDE.md's "Ingestion Eligibility: No Pool
/// Bifurcation"). Single-source (Alpaca), the Two-Tier Verification Cost Model's cheap nightly
/// tier - this is the full-universe cheap pre-filter itself, structurally distinct from
/// DmaComputationService/DmaIndicator (the narrower, dual-source-verified pipeline Gate's
/// Tier 1 gate reads). As of CLAUDE.md's "Universe-Wide Dynamic Discovery - Watchlist File
/// Retired from Production Path" (2026-08-05), DmaComputationService's own per-symbol compute
/// scope is driven by this class's <see cref="GetDma5AlignedTickersAsync"/> rather than a
/// fixed watchlist - the two remain structurally distinct services (this one is the
/// full-universe alignment tracker; DmaComputationService is the narrower, dual-source-verified
/// per-candidate pipeline), they are just no longer scoped independently of one another.
///
/// Complexity target: bootstrap (or a detected trading-calendar gap, or the fixed 30-night
/// re-anchor) costs a bounded O(w) per term per ticker; every other night costs O(1) per term
/// per ticker via RollingDmaCalculator's incremental drop-oldest/add-newest math - the nightly
/// total is O(N) across the full universe, never O(N x w), the CapitalFill anti-pattern (full
/// recompute from scratch, one round-trip per ticker per night) this component exists to avoid.
/// Reads/writes are chunked in batches (matching UniverseBarSweepRunner's own batching
/// discipline) to keep DB round-trips and per-transaction size bounded at full-universe scale.
/// </summary>
public class RollingDmaStateService
{
    private const string Resolution = "1Day";

    // Bump when the incremental formula itself changes - see RollingDmaState.CalculationVersion's
    // own doc comment for what this means for a mutable (not append-only) tracker.
    public const int CalculationVersion = 1;

    // Fixed interval, per CLAUDE.md's "Rolling DMA State Machine - Cadence Decisions Locked" -
    // periodic drift correction against accumulated incremental-rounding error, independent of
    // (and NOT to be conflated with) the separate, immediate re-entry bootstrap trigger below.
    public const int ReanchorIntervalNights = 30;

    // Chunking for DB round-trip/transaction-size bounding, same shape as
    // UniverseBarSweepRunner.ConfirmedMaxBatchSize - a different bottleneck (SQL round trips,
    // not Alpaca HTTP calls), kept as its own constant rather than a cross-component reference.
    public const int TickerBatchSize = 500;

    // Wide enough to comfortably cover the largest term (60 trading days) plus a generous
    // holiday/weekend buffer, so the single per-batch bar query below never has to be repeated
    // per ticker - 150 calendar days is >= ~107 trading days at the normal 5/7 ratio.
    public const int LookbackCalendarDays = 150;

    private readonly CalculatorDbContext _dbContext;
    private readonly ITradingCalendarService _calendarService;
    private readonly ILogger<RollingDmaStateService> _logger;

    public RollingDmaStateService(
        CalculatorDbContext dbContext,
        ITradingCalendarService calendarService,
        ILogger<RollingDmaStateService> logger)
    {
        _dbContext = dbContext;
        _calendarService = calendarService;
        _logger = logger;
    }

    public async Task RunAsync(DateOnly snapshotDate, CancellationToken cancellationToken)
    {
        var tickers = await _dbContext.UniverseSnapshots
            .Where(s => s.SnapshotDate == snapshotDate)
            .Select(s => s.Ticker)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (tickers.Count == 0)
        {
            // Fail-closed, same precedent as UniverseBarSweepRunner: no snapshot for this date
            // means nothing to process, no fallback to a stale prior-day snapshot.
            _logger.LogWarning(
                "[ROLLING-DMA]: no UniverseSnapshot rows found for {SnapshotDate} - nothing to process", snapshotDate);
            return;
        }

        // Visibility only, per CLAUDE.md's fail-closed-everywhere principle - a known-partial
        // day's surviving tickers are still processed exactly as before (no behavior change),
        // this only makes the gap loud instead of silent. A missing coverage row (e.g. a date
        // predating this check) is not itself treated as a problem - fails open to the existing
        // behavior, same as UniverseBarSweepRunner's identical check.
        var coverage = await _dbContext.UniverseSnapshotCoverages
            .SingleOrDefaultAsync(c => c.SnapshotDate == snapshotDate, cancellationToken);
        if (coverage is not null && coverage.SourcesSucceeded != UniverseSourceCoverage.All)
        {
            _logger.LogWarning(
                "[ROLLING-DMA]: {SnapshotDate}'s universe is KNOWN-PARTIAL (sources succeeded: {SourcesSucceeded}) " +
                "- processing the {TickerCount} ticker(s) actually present, but at least one exchange's tickers are " +
                "silently absent from tonight's universe",
                snapshotDate, coverage.SourcesSucceeded, tickers.Count);
        }

        var calendarStart = snapshotDate.AddDays(-LookbackCalendarDays);

        IReadOnlySet<DateOnly> openTradingDays;
        try
        {
            openTradingDays = await _calendarService.GetOpenTradingDaysAsync(calendarStart, snapshotDate, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail-closed, same as every other ComputationService in this codebase: without a
            // real trading calendar there is no way to distinguish a genuine gap from a normal
            // weekend/holiday closure, so nothing is processed this run.
            _logger.LogError(ex,
                "[ROLLING-DMA]: failed to fetch trading calendar for {SnapshotDate} - skipping this run entirely", snapshotDate);
            return;
        }

        // The distinction the task explicitly calls for: a cold-start run touches essentially
        // the entire universe via the O(w) bootstrap branch simultaneously - a genuine one-time
        // cost spike, not the steady-state O(N) this component is designed to deliver every
        // night after. Logged once, clearly, so this isn't later mistaken for a regression.
        var hasAnyExistingState = await _dbContext.RollingDmaStates
            .AnyAsync(s => s.CalculationVersion == CalculationVersion, cancellationToken);

        if (!hasAnyExistingState)
        {
            _logger.LogInformation(
                "[ROLLING-DMA]: BOOTSTRAP PASS for {SnapshotDate} - no existing rolling state found under " +
                "CalculationVersion {CalculationVersion}; all {TickerCount} ticker(s) in tonight's universe will " +
                "undergo a full O(w) trailing-window fill. Expect an elevated one-time run cost tonight only - " +
                "every subsequent night is the O(1)-per-ticker steady-state incremental pass.",
                snapshotDate, CalculationVersion, tickers.Count);
        }
        else
        {
            _logger.LogInformation(
                "[ROLLING-DMA]: STEADY-STATE INCREMENTAL PASS for {SnapshotDate} - existing rolling state found; " +
                "only tickers newly appearing in tonight's {TickerCount}-ticker universe (or recovering from a " +
                "detected trading-calendar gap) pay the full O(w) bootstrap cost, every other ticker updates in O(1).",
                snapshotDate, tickers.Count);
        }

        var bootstrappedCount = 0;
        var incrementalCount = 0;
        var gapRecoveredCount = 0;
        var reanchoredTermCount = 0;
        var noNewDataCount = 0;
        var noBarsCount = 0;
        var crossingCount = 0;

        var calendarStartOffset = new DateTimeOffset(calendarStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        // Upper-bounded at snapshotDate (inclusive) - without this, a run for an earlier date
        // could pick up bars already ingested under a later date (e.g. a backfill/correction
        // landing out of order), silently processing data this run should not yet know about
        // and breaking the point-in-time meaning of "as of snapshotDate."
        var snapshotDateOffset = new DateTimeOffset(snapshotDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        foreach (var batch in Chunk(tickers, TickerBatchSize))
        {
            var existingStateRows = await _dbContext.RollingDmaStates
                .Where(s => batch.Contains(s.Ticker) && s.CalculationVersion == CalculationVersion)
                .ToListAsync(cancellationToken);
            var statesByTicker = existingStateRows
                .GroupBy(s => s.Ticker)
                .ToDictionary(g => g.Key, g => g.ToDictionary(s => s.Term));

            var barRows = await _dbContext.RawOhlcvBars
                .Where(b => batch.Contains(b.Symbol)
                    && b.Resolution == Resolution
                    && b.Source == SourceType.Alpaca
                    && b.Timestamp >= calendarStartOffset
                    && b.Timestamp <= snapshotDateOffset)
                .OrderBy(b => b.Timestamp)
                .Select(b => new { b.Symbol, b.Timestamp, b.Close, b.IngestedAt })
                .ToListAsync(cancellationToken);

            // Narrow, evidence-scoped exception - see TiingoCloseCorrectionResolver's own doc
            // comment for the full citation and scope boundary. Matches DmaComputationService's
            // existing per-symbol correction behavior so RollingDmaStates' SMA values never
            // silently diverge from Gate's authoritative DmaIndicators values whenever a
            // corrected bar falls inside a ticker's rolling window.
            var correctedCloseByKey = await TiingoCloseCorrectionResolver.GetCorrectedClosesAsync(_dbContext, batch, cancellationToken);

            var barsByTicker = barRows
                .GroupBy(b => b.Symbol)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(b => b.Timestamp)
                        // Raw bars are append-only: same dedup shape as every other
                        // ComputationService - only the most recently ingested row per date is
                        // a live input.
                        .Select(dg => dg.OrderByDescending(b => b.IngestedAt).First())
                        .OrderBy(b => b.Timestamp)
                        .Select(b =>
                        {
                            var date = DateOnly.FromDateTime(b.Timestamp.UtcDateTime);
                            var close = correctedCloseByKey.TryGetValue((g.Key, date), out var corrected) ? corrected : b.Close;
                            return (Date: date, Close: close);
                        })
                        .ToList());

            foreach (var ticker in batch)
            {
                if (!barsByTicker.TryGetValue(ticker, out var bars) || bars.Count == 0)
                {
                    _logger.LogInformation("[ROLLING-DMA]: {Ticker}: no bars found, nothing to compute", ticker);
                    noBarsCount++;
                    continue;
                }

                var hasCompleteExistingState = statesByTicker.TryGetValue(ticker, out var existingTermStates)
                    && DmaCalculator.Windows.All(existingTermStates!.ContainsKey);

                if (!hasCompleteExistingState)
                {
                    // Brand new ticker (never tracked before) or one re-entering the universe
                    // after an absence - CLAUDE.md's re-entry bootstrap trigger, which runs
                    // immediately on this very run, on its own clock, independent of the
                    // 30-night re-anchor cadence.
                    BootstrapOrRecompute(ticker, bars, statesByTicker, existingTermStates);
                    bootstrappedCount++;
                    continue;
                }

                var termStates = existingTermStates!;
                var lastBarDate = termStates[DmaCalculator.Windows[0]].LastBarDate;
                var newBars = bars.Where(b => b.Date > lastBarDate).ToList();

                if (newBars.Count == 0)
                {
                    // No fresh bar for this ticker tonight (e.g. a data gap on the ingestion
                    // side) - left as-is, picked back up whenever a new bar does arrive. Not
                    // treated as an error, same "ambiguous, not a failure" precedent as
                    // NoDataForRange elsewhere in this codebase.
                    noNewDataCount++;
                    continue;
                }

                var barDateSet = bars.Select(b => b.Date).ToHashSet();
                var missingDates = openTradingDays
                    .Where(d => d > lastBarDate && d <= newBars[^1].Date && !barDateSet.Contains(d))
                    .OrderBy(d => d)
                    .ToList();

                if (missingDates.Count > 0)
                {
                    // A real gap: incremental drop-oldest/add-newest math cannot be trusted
                    // once an expected trading day is silently missing, since the state no
                    // longer reflects a window of exactly Term real trading days. Same
                    // fail-closed instinct as DmaGapChecker, but recovered via a full recompute
                    // (this is persistent state, not a one-off computed value) rather than
                    // simply skipped.
                    _logger.LogWarning(
                        "[ROLLING-DMA]: {Ticker}: trading-calendar gap detected between {LastBarDate} and " +
                        "{NewestDate} - missing expected trading date(s): {MissingDates} - incremental math " +
                        "cannot be trusted across a gap, forcing a full recompute instead",
                        ticker, lastBarDate, newBars[^1].Date, string.Join(", ", missingDates));
                    BootstrapOrRecompute(ticker, bars, statesByTicker, termStates);
                    gapRecoveredCount++;
                    continue;
                }

                var barDateIndex = new Dictionary<DateOnly, int>();
                for (var i = 0; i < bars.Count; i++)
                {
                    barDateIndex[bars[i].Date] = i;
                }
                var closes = bars.Select(b => b.Close).ToList();

                foreach (var newBar in newBars)
                {
                    var idx = barDateIndex[newBar.Date];

                    foreach (var term in DmaCalculator.Windows)
                    {
                        var state = termStates[term];
                        var reanchorDue = state.UpdatesSinceAnchor >= ReanchorIntervalNights - 1;
                        var dropIndex = idx - term;

                        RollingDmaCalculator.UpdateResult result;
                        bool resetAnchorCounter;

                        if (reanchorDue)
                        {
                            result = RollingDmaCalculator.ComputeSumFromScratch(closes.Take(idx + 1).ToList(), term);
                            resetAnchorCounter = true;
                            reanchoredTermCount++;
                            _logger.LogInformation(
                                "[ROLLING-DMA]: {Ticker} term {Term}: {ReanchorInterval}-night re-anchor - " +
                                "recomputing from scratch to correct incremental drift",
                                ticker, term, ReanchorIntervalNights);
                        }
                        else if (state.BarCount >= term && dropIndex < 0)
                        {
                            // Defensive only - should be unreachable given LookbackCalendarDays'
                            // margin over the largest term. If the lookback window was ever too
                            // narrow this recovers safely (full recompute) rather than reading a
                            // bogus index.
                            _logger.LogError(
                                "[ROLLING-DMA]: {Ticker} term {Term}: dropped-bar index computed negative " +
                                "({DropIndex}) - lookback window too narrow for this term, forcing a full " +
                                "recompute instead",
                                ticker, term, dropIndex);
                            result = RollingDmaCalculator.ComputeSumFromScratch(closes.Take(idx + 1).ToList(), term);
                            resetAnchorCounter = true;
                        }
                        else
                        {
                            var droppedClose = state.BarCount >= term ? closes[dropIndex] : (decimal?)null;
                            result = RollingDmaCalculator.ApplyIncrementalUpdate(state.RollingSum, state.BarCount, term, newBar.Close, droppedClose);
                            resetAnchorCounter = false;
                        }

                        state.UpdatesSinceAnchor = resetAnchorCounter ? 0 : state.UpdatesSinceAnchor + 1;
                        state.RollingSum = result.RollingSum;
                        state.BarCount = result.BarCount;
                        state.Value = result.Value;
                        state.InsufficientHistory = result.Value is null;
                        state.LastBarDate = newBar.Date;
                        state.UpdatedAt = DateTimeOffset.UtcNow;
                    }

                    var termValues = new RollingDmaAlignmentEvaluator.TermValues(
                        termStates[5].Value, termStates[15].Value, termStates[30].Value, termStates[60].Value);
                    var newAlignment = RollingDmaAlignmentEvaluator.EvaluateAlignment(termValues);

                    foreach (var term in DmaCalculator.Windows)
                    {
                        // (DmaAlignmentTerm)term is a direct cast, not a lookup - DmaAlignmentTerm's
                        // values are explicitly pinned to match DmaCalculator.Windows (5/15/30/60)
                        // exactly. RollingDmaState.Term itself stays a plain int (the real DMA
                        // window size, unaffected by this rename); only the alignment-flag identity
                        // written to DmaCrossingLogEntry.Term uses the named enum.
                        var alignmentTerm = (DmaAlignmentTerm)term;
                        var state = termStates[term];
                        var wasAligned = state.IsAligned;
                        var isAligned = newAlignment[alignmentTerm];
                        if (wasAligned != isAligned)
                        {
                            _dbContext.DmaCrossingLogEntries.Add(new DmaCrossingLogEntry
                            {
                                Ticker = ticker,
                                Term = alignmentTerm,
                                Direction = isAligned ? DmaCrossingDirection.Entered : DmaCrossingDirection.Exited,
                                TransitionDate = newBar.Date,
                                CalculationVersion = CalculationVersion,
                                LoggedAt = DateTimeOffset.UtcNow
                            });
                            crossingCount++;
                        }
                        state.IsAligned = isAligned;
                    }
                }

                incrementalCount++;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation(
            "[ROLLING-DMA]: run for {SnapshotDate} complete - {Total} ticker(s) in universe: {Bootstrapped} " +
            "bootstrapped, {Incremental} incremental, {GapRecovered} gap-recovered, {Reanchored} term re-anchor(s), " +
            "{NoNewData} with no new data, {NoBars} with no bars at all, {Crossings} crossing-log entrie(s)",
            snapshotDate, tickers.Count, bootstrappedCount, incrementalCount, gapRecoveredCount, reanchoredTermCount,
            noNewDataCount, noBarsCount, crossingCount);
    }

    /// <summary>
    /// Bounded O(w) full recompute of all four terms, seeding fresh RollingDmaState rows for a
    /// ticker with no prior tracked state at all, or overwriting existing rows in place when
    /// called for gap recovery. Either way, this is initialization/recovery, not a genuine
    /// alignment transition - no DmaCrossingLogEntry rows are emitted here, since there is no
    /// prior IsAligned value on a brand-new (or freshly-recomputed) row to have transitioned
    /// from.
    /// </summary>
    private void BootstrapOrRecompute(
        string ticker,
        List<(DateOnly Date, decimal Close)> bars,
        Dictionary<string, Dictionary<int, RollingDmaState>> statesByTicker,
        Dictionary<int, RollingDmaState>? existingTermStates)
    {
        var closes = bars.Select(b => b.Close).ToList();
        var lastBarDate = bars[^1].Date;

        var termStates = existingTermStates ?? new Dictionary<int, RollingDmaState>();

        foreach (var term in DmaCalculator.Windows)
        {
            var result = RollingDmaCalculator.ComputeSumFromScratch(closes, term);

            if (!termStates.TryGetValue(term, out var state))
            {
                state = new RollingDmaState
                {
                    Ticker = ticker,
                    Term = term,
                    CalculationVersion = CalculationVersion
                };
                _dbContext.RollingDmaStates.Add(state);
                termStates[term] = state;
            }

            state.RollingSum = result.RollingSum;
            state.BarCount = result.BarCount;
            state.Value = result.Value;
            state.InsufficientHistory = result.Value is null;
            state.LastBarDate = lastBarDate;
            state.UpdatesSinceAnchor = 0;
            state.UpdatedAt = DateTimeOffset.UtcNow;
        }

        statesByTicker[ticker] = termStates;

        var termValues = new RollingDmaAlignmentEvaluator.TermValues(
            termStates[5].Value, termStates[15].Value, termStates[30].Value, termStates[60].Value);
        var alignment = RollingDmaAlignmentEvaluator.EvaluateAlignment(termValues);

        foreach (var term in DmaCalculator.Windows)
        {
            termStates[term].IsAligned = alignment[(DmaAlignmentTerm)term];
        }
    }

    /// <summary>
    /// CLAUDE.md's "Calculator Ticker Source: Watchlist + Discovered-Aligned Union, Not a
    /// Swap" (2026-07-30) - the discovery half of the union. Reads current, sticky
    /// RollingDmaState (not a snapshot for a specific past date): FullStackAligned's own doc
    /// comment already establishes it can never flip independently of, or precede, Term5/
    /// Term15/Term30, so a true value here means the full 5&gt;15&gt;30&gt;60 stack is aligned as of
    /// whatever date this state machine most recently, successfully processed - "as of the
    /// current run date" in the normal case, since Worker.cs calls RunAsync for the same
    /// runDate immediately before calling this. Deliberately not filtered by LastBarDate: a
    /// ticker's alignment persists (sticky) across a legitimate no-new-data day (e.g. a
    /// holiday), so requiring LastBarDate == runDate would incorrectly drop still-genuinely-
    /// aligned tickers on any day RunAsync had nothing new to fold in for them.
    /// </summary>
    public async Task<List<string>> GetFullyAlignedTickersAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.RollingDmaStates
            .Where(s => s.Term == (int)DmaAlignmentTerm.FullStackAligned
                && s.CalculationVersion == CalculationVersion
                && s.IsAligned)
            .OrderBy(s => s.Ticker)
            .Select(s => s.Ticker)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// CLAUDE.md's "Universe-Wide Dynamic Discovery - Watchlist File Retired from Production
    /// Path" (2026-08-05) - the replacement discovery-scoping source for
    /// <see cref="GetFullyAlignedTickersAsync"/>, filtered to Term5 rather than
    /// FullStackAligned so Calculator's discovery filter uses the same "aligned" definition as
    /// Gate's actual Tier 1 entry gate (DMA-5-only, per the 2026-08-04 relaxation in
    /// GateQualityScorer.Score - see also "DMA Verification: Blast-Radius Scoping" and "RVOL
    /// Floor Relaxed" the same date). FullStackAligned (5&gt;15&gt;30&gt;60) is a strictly
    /// stricter standard than Gate now actually requires to admit a candidate, so scoping
    /// discovery to it left genuinely Gate-eligible tickers undiscovered.
    ///
    /// Same current/sticky-state semantics as <see cref="GetFullyAlignedTickersAsync"/> - not
    /// filtered by LastBarDate, for the identical reason (alignment persists across a
    /// legitimate no-new-data day).
    ///
    /// Deliberately does not replace or modify <see cref="GetFullyAlignedTickersAsync"/> -
    /// left in place per that CLAUDE.md decision's own explicit instruction ("other code may
    /// still reference it; that will be audited and re-pointed in a later task, not this
    /// one"). This method is not yet wired into Worker.cs's discovery union.
    /// </summary>
    public async Task<List<string>> GetDma5AlignedTickersAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.RollingDmaStates
            .Where(s => s.Term == (int)DmaAlignmentTerm.Term5
                && s.CalculationVersion == CalculationVersion
                && s.IsAligned)
            .OrderBy(s => s.Ticker)
            .Select(s => s.Ticker)
            .ToListAsync(cancellationToken);
    }

    private static IEnumerable<List<string>> Chunk(IReadOnlyList<string> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.Skip(i).Take(size).ToList();
    }
}
