using Drps.Calculator.Calendar;
using Drps.Calculator.Indicators;
using Drps.Calculator.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Calculator.Dma;

/// <summary>
/// Engine-only computation (Anti-Spaghetti Rule #1: Engines never decide) - reads raw bars
/// and computes the DMA-5/15/30/60 stack regardless of the underlying bars' verification
/// status (verification is resolved live, at read time, by DmaVerificationJoinService - a
/// snapshot stored here would go stale the moment a bar's status changes later). No
/// bucketing, no crossover/alignment detection - that's Gate-shaped selection logic.
/// </summary>
public class DmaComputationService
{
    private const string Resolution = "1Day";

    // Bump when the DMA formula itself changes. A version bump adds new rows; it never
    // rewrites the values already stored under an earlier version, per the Immutability &
    // Extensibility rule.
    public const int CalculationVersion = 1;

    private readonly CalculatorDbContext _dbContext;
    private readonly ITradingCalendarService _calendarService;
    private readonly ILogger<DmaComputationService> _logger;

    public DmaComputationService(
        CalculatorDbContext dbContext,
        ITradingCalendarService calendarService,
        ILogger<DmaComputationService> logger)
    {
        _dbContext = dbContext;
        _calendarService = calendarService;
        _logger = logger;
    }

    public async Task ComputeAsync(string symbol, TickerSourceOrigin sourceOrigin, CancellationToken cancellationToken)
    {
        // One query per symbol (not one per DMA window). Deliberately no join/filter against
        // BarVerification: a DMA value is computed regardless of the underlying bars'
        // current verification status, since a cached verification snapshot at compute time
        // would go stale the moment a bar's status changes later (e.g. a second source
        // catches up). Verification is a live, read-time concern - see
        // Drps.Calculator/Verification/DmaVerificationJoinService.cs. Alpaca is used here as
        // the execution venue / primary source per CLAUDE.md.
        var rows = await _dbContext.RawOhlcvBars
            .Where(bar => bar.Symbol == symbol
                && bar.Resolution == Resolution
                && bar.Source == SourceType.Alpaca)
            .OrderBy(bar => bar.Timestamp)
            .Select(bar => new { bar.Timestamp, bar.Close, bar.IngestedAt })
            .ToListAsync(cancellationToken);

        // Narrow, evidence-scoped exception - see GetTiingoCorrectedClosesAsync's own doc
        // comment for the full citation and scope boundary.
        var correctedCloseByDate = await GetTiingoCorrectedClosesAsync(symbol, cancellationToken);

        // Raw bars are append-only: a source re-ingested across separate runs can have
        // multiple historical rows for the same (Symbol, Timestamp, Resolution). Same dedup
        // shape as BarReconciliationService - only the most recently ingested row per date
        // is a live input to the average.
        var bars = rows
            .GroupBy(r => r.Timestamp)
            .Select(g => g.OrderByDescending(r => r.IngestedAt).First())
            .OrderBy(r => r.Timestamp)
            .Select(r =>
            {
                var date = DateOnly.FromDateTime(r.Timestamp.UtcDateTime);
                var close = correctedCloseByDate.TryGetValue(date, out var corrected) ? corrected : r.Close;
                return new DmaCalculator.DmaBarInput(date, close);
            })
            .ToList();

        if (bars.Count == 0)
        {
            _logger.LogInformation("[DMA]: {Symbol}: no bars found, nothing to compute", symbol);
            return;
        }

        IReadOnlySet<DateOnly> openTradingDays;
        try
        {
            openTradingDays = await _calendarService.GetOpenTradingDaysAsync(
                bars[0].Date, bars[^1].Date, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail-closed: without a real trading calendar there is no way to distinguish a
            // genuine ingestion gap from a normal weekend/holiday closure, so nothing is
            // computed for this symbol this run rather than silently falling back to the old
            // gap-blind behavior. OperationCanceledException is deliberately excluded so a
            // real shutdown still propagates to Worker's own cancellation-aware catch instead
            // of being logged-and-swallowed here.
            _logger.LogError(ex, "[DMA]: {Symbol}: failed to fetch trading calendar, skipping DMA computation for this run", symbol);
            return;
        }

        var gapCheck = DmaGapChecker.Filter(bars, openTradingDays);

        foreach (var skippedWindow in gapCheck.SkippedWindows)
        {
            _logger.LogWarning(
                "[DMA]: {Symbol}: skipping DMA-{Window} for {WindowEndDate} - trading-calendar gap detected, missing expected trading date(s): {MissingDates}",
                symbol, skippedWindow.Window, skippedWindow.WindowEndDate,
                string.Join(", ", skippedWindow.MissingDates));
        }

        var results = gapCheck.ClearResults;

        // Single query per symbol (not one per DMA window), same discipline as the bar query
        // above. Informational only - per CLAUDE.md's Ex-Dividend Handling decision, this
        // never skips/excludes/alters a window's computed value, it only tags the row.
        var firstBarDate = bars[0].Date;
        var lastBarDate = bars[^1].Date;
        var exDividendDates = await _dbContext.RawExDividendObservations
            .Where(o => o.Symbol == symbol && o.ExDividendDate >= firstBarDate && o.ExDividendDate <= lastBarDate)
            .Select(o => o.ExDividendDate)
            .Distinct()
            .ToListAsync(cancellationToken);

        var annotatedResults = DmaExDividendAnnotator.Annotate(bars, results, exDividendDates);

        var existingKeys = await _dbContext.DmaIndicators
            .Where(d => d.Symbol == symbol && d.CalculationVersion == CalculationVersion)
            .Select(d => new { d.BarDate, d.Window })
            .ToListAsync(cancellationToken);
        var existingSet = existingKeys.Select(k => (k.BarDate, k.Window)).ToHashSet();

        // Same window-span reconstruction IndicatorWindowSpan already provides for RSI/RVOL/
        // ATR's ex-dividend annotation - reused here rather than re-derived, even though
        // DmaExDividendAnnotator itself predates this helper and still uses its own inline
        // math for the ex-div flag.
        var barDates = bars.Select(b => b.Date).ToList();
        var dateToIndex = IndicatorWindowSpan.BuildDateIndex(barDates);
        var correctedDateSet = correctedCloseByDate.Keys.ToHashSet();

        var addedCount = 0;
        foreach (var annotated in annotatedResults)
        {
            var result = annotated.Result;
            if (existingSet.Contains((result.Date, result.Window)))
            {
                continue;
            }

            var span = IndicatorWindowSpan.GetWindowSpan(barDates, dateToIndex[result.Date], result.Window);
            var hasTiingoCorrectedClose = IndicatorWindowSpan.ContainsAny(span, correctedDateSet);

            _dbContext.DmaIndicators.Add(new DmaIndicator
            {
                Symbol = symbol,
                BarDate = result.Date,
                Window = result.Window,
                Value = result.Value,
                HasExDividendEvent = annotated.HasExDividendEvent,
                HasTiingoCorrectedClose = hasTiingoCorrectedClose,
                TickerSourceOrigin = sourceOrigin,
                CalculationVersion = CalculationVersion,
                ComputedAt = DateTimeOffset.UtcNow
            });
            addedCount++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "[DMA]: {Symbol}: {BarCount} bar(s) evaluated, {AddedCount} new DMA row(s) added",
            symbol, bars.Count, addedCount);
    }

    // Narrow, evidence-scoped exception - see CLAUDE.md, "Reconciliation: Narrow Tiingo-Close
    // Exception for the OHL-Agrees/C-Disagrees Signature" (2026-07-17), and this session's
    // follow-up audit that found BarVerification.PrimarySourceValue had zero production
    // consumers despite being written whenever this exception fires. This closes that gap for
    // DMA specifically. NOT a general Alpaca-vs-Tiingo preference: only dates where
    // BarReconciliationService's own narrow OHL-agreed/Close-disagreed signature actually
    // fired (Discrepancy.ResolutionMethod == OhlAgreedCloseResolvedToTiingo) are substituted
    // here - every other disagreement shape, and every other source pair, still uses
    // Alpaca's raw Close from RawOhlcvBars, completely unchanged. Do not extend this logic to
    // any other source pair or disagreement shape without a matching CLAUDE.md decision.
    private async Task<Dictionary<DateOnly, decimal>> GetTiingoCorrectedClosesAsync(
        string symbol, CancellationToken cancellationToken)
    {
        var correctedTimestamps = await _dbContext.Discrepancies
            .Where(d => d.Symbol == symbol && d.ResolutionMethod == DiscrepancyResolutionMethod.OhlAgreedCloseResolvedToTiingo)
            .Select(d => d.Timestamp)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (correctedTimestamps.Count == 0)
        {
            return new Dictionary<DateOnly, decimal>();
        }

        var verifications = await _dbContext.BarVerifications
            .Where(v => v.Symbol == symbol && v.Resolution == Resolution && correctedTimestamps.Contains(v.Timestamp))
            .Select(v => new { v.Timestamp, v.PrimarySourceValue })
            .ToListAsync(cancellationToken);

        return verifications
            .Where(v => v.PrimarySourceValue.HasValue)
            .ToDictionary(
                v => DateOnly.FromDateTime(v.Timestamp.UtcDateTime),
                v => v.PrimarySourceValue!.Value);
    }
}
