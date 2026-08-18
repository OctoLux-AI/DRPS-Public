using Drps.Calculator.Calendar;
using Drps.Calculator.Indicators;
using Drps.Calculator.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Drps.Calculator.Rsi;

/// <summary>
/// Engine-only computation (Anti-Spaghetti Rule #1: Engines never decide) - reads
/// already-computed RSI rows and derives the first discrete difference (slope), then applies
/// the anti-whipsaw confirmation filter. WATCH-ONLY per CLAUDE.md's "RsiSlope / RsiConcavity:
/// Design Direction Locked" (2026-07-31): computed and persisted, no Adjuster multiplier, no
/// Execution exit-trigger wiring in this task.
///
/// Genuinely different input shape than DmaComputationService/RsiComputationService/
/// RvolComputationService/AtrComputationService, which all read RawOhlcvBar directly - this is
/// the first Calculator indicator computed from another indicator's own persisted output
/// (RsiIndicator), not from raw bars. Must therefore run AFTER RsiComputationService in the
/// same cycle (see Worker.cs's per-symbol loop ordering) - a stale/empty RsiIndicators table
/// for a symbol simply produces nothing here, same fail-open-to-no-data shape as every other
/// "no bars found" early return in this codebase.
/// </summary>
public class RsiSlopeComputationService
{
    // Bump when the RsiSlope formula itself changes. Independent of RsiComputationService's own
    // CalculationVersion - the two indicators version separately, same precedent as every other
    // pair of indicators in this codebase.
    public const int CalculationVersion = 1;

    private readonly CalculatorDbContext _dbContext;
    private readonly ITradingCalendarService _calendarService;
    private readonly CalculatorSettings _settings;
    private readonly ILogger<RsiSlopeComputationService> _logger;

    public RsiSlopeComputationService(
        CalculatorDbContext dbContext,
        ITradingCalendarService calendarService,
        IOptions<CalculatorSettings> settings,
        ILogger<RsiSlopeComputationService> logger)
    {
        _dbContext = dbContext;
        _calendarService = calendarService;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task ComputeAsync(string symbol, CancellationToken cancellationToken)
    {
        var lookback = _settings.RsiSlopeLookback;

        // Reads the CURRENT RSI calculation version only - same "only the latest formula
        // version is a live input" convention every other cross-indicator read in this
        // codebase already follows (e.g. RsiComputationService's own Tiingo-correction lookup
        // against BarVerification).
        var rsiRows = await _dbContext.RsiIndicators
            .Where(r => r.Symbol == symbol && r.CalculationVersion == RsiComputationService.CalculationVersion)
            .OrderBy(r => r.BarDate)
            .ToListAsync(cancellationToken);

        if (rsiRows.Count == 0)
        {
            _logger.LogInformation("[RSI-SLOPE]: {Symbol}: no RSI rows found, nothing to compute", symbol);
            return;
        }

        var series = rsiRows
            .Select(r => new RsiSlopeCalculator.RsiSlopeInput(r.BarDate, r.Value))
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
            _logger.LogError(ex, "[RSI-SLOPE]: {Symbol}: failed to fetch trading calendar, skipping RSI slope computation for this run", symbol);
            return;
        }

        var gapCheck = RsiSlopeGapChecker.Filter(series, lookback, openTradingDays);

        foreach (var skipped in gapCheck.SkippedResults)
        {
            _logger.LogWarning(
                "[RSI-SLOPE]: {Symbol}: skipping RSI slope for {Date} - trading-calendar gap detected in the underlying RSI series, missing expected trading date(s): {MissingDates}",
                symbol, skipped.Date, string.Join(", ", skipped.MissingDates));
        }

        var results = gapCheck.ClearResults;

        if (results.Count == 0)
        {
            _logger.LogInformation("[RSI-SLOPE]: {Symbol}: no gap-clear RSI slope results this run", symbol);
            return;
        }

        var existingKeys = await _dbContext.RsiSlopeIndicators
            .Where(r => r.Symbol == symbol && r.Lookback == lookback && r.CalculationVersion == CalculationVersion)
            .Select(r => r.BarDate)
            .ToListAsync(cancellationToken);
        var existingSet = existingKeys.ToHashSet();

        // Confirmation is evaluated over the full ordered, already-gap-clear results sequence -
        // this is the sequence that will actually be persisted, so a "streak" is judged against
        // what a future consumer will actually see.
        var directions = RsiSlopeConfirmationEvaluator.Evaluate(results.Select(r => r.Value).ToList());

        var rsiDates = rsiRows.Select(r => r.BarDate).ToList();
        var dateToIndex = IndicatorWindowSpan.BuildDateIndex(rsiDates);
        var exDividendDatesFromRsi = rsiRows.Where(r => r.HasExDividendEvent).Select(r => r.BarDate).ToHashSet();
        var correctedDatesFromRsi = rsiRows.Where(r => r.HasTiingoCorrectedClose).Select(r => r.BarDate).ToHashSet();

        var windowSize = lookback + 1;
        var addedCount = 0;

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (existingSet.Contains(result.Date))
            {
                continue;
            }

            var span = IndicatorWindowSpan.GetWindowSpan(rsiDates, dateToIndex[result.Date], windowSize);
            var hasExDividendEvent = IndicatorWindowSpan.ContainsAny(span, exDividendDatesFromRsi);
            var hasTiingoCorrectedClose = IndicatorWindowSpan.ContainsAny(span, correctedDatesFromRsi);

            _dbContext.RsiSlopeIndicators.Add(new RsiSlopeIndicator
            {
                Symbol = symbol,
                BarDate = result.Date,
                Lookback = result.Lookback,
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
            "[RSI-SLOPE]: {Symbol}: {RsiCount} RSI row(s) evaluated (lookback {Lookback}), {AddedCount} new RSI slope row(s) added",
            symbol, rsiRows.Count, lookback, addedCount);
    }
}
