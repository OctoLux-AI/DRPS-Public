using Drps.Calculator.Calendar;
using Drps.Calculator.Indicators;
using Drps.Calculator.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Drps.Calculator.Rsi;

/// <summary>
/// Engine-only computation (Anti-Spaghetti Rule #1: Engines never decide) - reads
/// already-computed RsiSlope rows and derives the second discrete difference (concavity), then
/// applies its own (longer/stricter) anti-whipsaw confirmation filter. WATCH-ONLY per
/// CLAUDE.md's "RsiSlope / RsiConcavity: Design Direction Locked" (2026-07-31): computed and
/// persisted only - not wired into any consumer at all, unlike RsiSlope, which at least has two
/// named future consumers already approved in principle.
///
/// Must run AFTER RsiSlopeComputationService in the same cycle (see Worker.cs's per-symbol loop
/// ordering) - reads RsiSlopeIndicators, not RsiIndicators directly, matching the locked
/// design's own formula (RsiConcavity[t] = RsiSlope[t] - RsiSlope[t-1], not re-derived from RSI
/// independently).
/// </summary>
public class RsiConcavityComputationService
{
    // Bump when the RsiConcavity formula itself changes. Independent of
    // RsiSlopeComputationService's own CalculationVersion.
    public const int CalculationVersion = 1;

    private readonly CalculatorDbContext _dbContext;
    private readonly ITradingCalendarService _calendarService;
    private readonly CalculatorSettings _settings;
    private readonly ILogger<RsiConcavityComputationService> _logger;

    public RsiConcavityComputationService(
        CalculatorDbContext dbContext,
        ITradingCalendarService calendarService,
        IOptions<CalculatorSettings> settings,
        ILogger<RsiConcavityComputationService> logger)
    {
        _dbContext = dbContext;
        _calendarService = calendarService;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task ComputeAsync(string symbol, CancellationToken cancellationToken)
    {
        var slopeLookback = _settings.RsiSlopeLookback;

        // Reads the CURRENT slope lookback/version only - same "only the latest config/formula
        // version is a live input" convention as RsiSlopeComputationService's own read of
        // RsiIndicators.
        var slopeRows = await _dbContext.RsiSlopeIndicators
            .Where(r => r.Symbol == symbol
                && r.Lookback == slopeLookback
                && r.CalculationVersion == RsiSlopeComputationService.CalculationVersion)
            .OrderBy(r => r.BarDate)
            .ToListAsync(cancellationToken);

        if (slopeRows.Count == 0)
        {
            _logger.LogInformation("[RSI-CONCAVITY]: {Symbol}: no RSI slope rows found, nothing to compute", symbol);
            return;
        }

        var series = slopeRows
            .Select(r => new RsiConcavityCalculator.RsiConcavityInput(r.BarDate, r.Value))
            .ToList();

        IReadOnlySet<DateOnly> openTradingDays;
        try
        {
            openTradingDays = await _calendarService.GetOpenTradingDaysAsync(
                series[0].Date, series[^1].Date, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail-closed, same as every other indicator in this codebase.
            _logger.LogError(ex, "[RSI-CONCAVITY]: {Symbol}: failed to fetch trading calendar, skipping RSI concavity computation for this run", symbol);
            return;
        }

        var gapCheck = RsiConcavityGapChecker.Filter(series, openTradingDays);

        foreach (var skipped in gapCheck.SkippedResults)
        {
            _logger.LogWarning(
                "[RSI-CONCAVITY]: {Symbol}: skipping RSI concavity for {Date} - trading-calendar gap detected between consecutive RSI slope readings, missing expected trading date(s): {MissingDates}",
                symbol, skipped.Date, string.Join(", ", skipped.MissingDates));
        }

        var results = gapCheck.ClearResults;

        if (results.Count == 0)
        {
            _logger.LogInformation("[RSI-CONCAVITY]: {Symbol}: no gap-clear RSI concavity results this run", symbol);
            return;
        }

        var existingKeys = await _dbContext.RsiConcavityIndicators
            .Where(r => r.Symbol == symbol && r.SlopeLookback == slopeLookback && r.CalculationVersion == CalculationVersion)
            .Select(r => r.BarDate)
            .ToListAsync(cancellationToken);
        var existingSet = existingKeys.ToHashSet();

        // Concavity's OWN, longer/stricter confirmation filter - see
        // RsiConcavityConfirmationEvaluator's own doc comment for why this is not
        // RsiSlopeConfirmationEvaluator reused at face value.
        var directions = RsiConcavityConfirmationEvaluator.Evaluate(results.Select(r => r.Value).ToList());

        var slopeDates = slopeRows.Select(r => r.BarDate).ToList();
        var dateToIndex = IndicatorWindowSpan.BuildDateIndex(slopeDates);
        var exDividendDatesFromSlope = slopeRows.Where(r => r.HasExDividendEvent).Select(r => r.BarDate).ToHashSet();
        var correctedDatesFromSlope = slopeRows.Where(r => r.HasTiingoCorrectedClose).Select(r => r.BarDate).ToHashSet();

        var addedCount = 0;

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (existingSet.Contains(result.Date))
            {
                continue;
            }

            // Fixed 2-wide window (this reading plus its immediate slope predecessor) - see
            // RsiConcavityGapChecker.WindowSize's own doc comment.
            var span = IndicatorWindowSpan.GetWindowSpan(slopeDates, dateToIndex[result.Date], RsiConcavityGapChecker.WindowSize);
            var hasExDividendEvent = IndicatorWindowSpan.ContainsAny(span, exDividendDatesFromSlope);
            var hasTiingoCorrectedClose = IndicatorWindowSpan.ContainsAny(span, correctedDatesFromSlope);

            _dbContext.RsiConcavityIndicators.Add(new RsiConcavityIndicator
            {
                Symbol = symbol,
                BarDate = result.Date,
                SlopeLookback = slopeLookback,
                Value = result.Value,
                ConfirmedDirection = directions[i],
                HasExDividendEvent = hasExDividendEvent,
                HasTiingoCorrectedClose = hasTiingoCorrectedClose,
                CalculationVersion = CalculationVersion,
                ComputedAt = DateTimeOffset.UtcNow
            });
            addedCount++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "[RSI-CONCAVITY]: {Symbol}: {SlopeCount} RSI slope row(s) evaluated, {AddedCount} new RSI concavity row(s) added",
            symbol, slopeRows.Count, addedCount);
    }
}
