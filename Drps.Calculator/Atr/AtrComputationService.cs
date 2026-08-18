using Drps.Calculator.Calendar;
using Drps.Calculator.Indicators;
using Drps.Calculator.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Calculator.Atr;

/// <summary>
/// Engine-only computation (Anti-Spaghetti Rule #1: Engines never decide) - reads raw bars
/// and computes 14-period Wilder's ATR regardless of the underlying bars' verification
/// status (verification is resolved live, at read time, by AtrVerificationJoinService).
/// No trailing-stop/exit logic, no threshold classification - that's Gate/strategy-shaped
/// selection logic; this task only produces and stores the ATR number. Structurally
/// parallel to DmaComputationService/RsiComputationService/RvolComputationService, kept as
/// a separate class rather than unified into one generic indicator runner - same reasoning
/// as IngestionRunner/ExDividendIngestionRunner staying separate.
/// </summary>
public class AtrComputationService
{
    private const string Resolution = "1Day";

    // Bump when the ATR formula itself changes. Independent of the other indicators' own
    // CalculationVersion - each indicator versions separately.
    public const int CalculationVersion = 1;

    private readonly CalculatorDbContext _dbContext;
    private readonly ITradingCalendarService _calendarService;
    private readonly ILogger<AtrComputationService> _logger;

    public AtrComputationService(
        CalculatorDbContext dbContext,
        ITradingCalendarService calendarService,
        ILogger<AtrComputationService> logger)
    {
        _dbContext = dbContext;
        _calendarService = calendarService;
        _logger = logger;
    }

    public async Task ComputeAsync(string symbol, TickerSourceOrigin sourceOrigin, CancellationToken cancellationToken)
    {
        // One query per symbol, same discipline as DmaComputationService/
        // RsiComputationService/RvolComputationService. Deliberately no join/filter against
        // BarVerification - see AtrVerificationJoinService for why.
        var rows = await _dbContext.RawOhlcvBars
            .Where(bar => bar.Symbol == symbol
                && bar.Resolution == Resolution
                && bar.Source == SourceType.Alpaca)
            .OrderBy(bar => bar.Timestamp)
            .Select(bar => new { bar.Timestamp, bar.High, bar.Low, bar.Close, bar.IngestedAt })
            .ToListAsync(cancellationToken);

        // Narrow, evidence-scoped exception - see GetTiingoCorrectedClosesAsync's own doc
        // comment for the full citation and scope boundary. Only Close is ever substituted -
        // the OHL-agreed exception exists precisely because Open/High/Low already agreed
        // within tolerance, so there is no "corrected" High/Low value to apply; those two
        // fields stay Alpaca's raw values below regardless of this lookup.
        var correctedCloseByDate = await GetTiingoCorrectedClosesAsync(symbol, cancellationToken);

        // Raw bars are append-only: same dedup shape as DmaComputationService/
        // BarReconciliationService - only the most recently ingested row per date is a live
        // input.
        var bars = rows
            .GroupBy(r => r.Timestamp)
            .Select(g => g.OrderByDescending(r => r.IngestedAt).First())
            .OrderBy(r => r.Timestamp)
            .Select(r =>
            {
                var date = DateOnly.FromDateTime(r.Timestamp.UtcDateTime);
                var close = correctedCloseByDate.TryGetValue(date, out var corrected) ? corrected : r.Close;
                return new AtrCalculator.AtrBarInput(date, r.High, r.Low, close);
            })
            .ToList();

        if (bars.Count == 0)
        {
            _logger.LogInformation("[ATR]: {Symbol}: no bars found, nothing to compute", symbol);
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
            // Fail-closed, same as DmaComputationService/RsiComputationService.
            _logger.LogError(ex, "[ATR]: {Symbol}: failed to fetch trading calendar, skipping ATR computation for this run", symbol);
            return;
        }

        var gapCheck = AtrGapChecker.Filter(bars, openTradingDays);

        foreach (var skipped in gapCheck.SkippedResults)
        {
            _logger.LogWarning(
                "[ATR]: {Symbol}: skipping ATR-{Period} for {Date} - trading-calendar gap detected, missing expected trading date(s): {MissingDates}",
                symbol, AtrCalculator.Period, skipped.Date, string.Join(", ", skipped.MissingDates));
        }

        var results = gapCheck.ClearResults;

        // Single query per symbol, same discipline as the other indicators' ex-div query.
        var firstBarDate = bars[0].Date;
        var lastBarDate = bars[^1].Date;
        var exDividendDates = await _dbContext.RawExDividendObservations
            .Where(o => o.Symbol == symbol && o.ExDividendDate >= firstBarDate && o.ExDividendDate <= lastBarDate)
            .Select(o => o.ExDividendDate)
            .Distinct()
            .ToListAsync(cancellationToken);

        var annotatedResults = AtrExDividendAnnotator.Annotate(bars, results, exDividendDates);

        var existingKeys = await _dbContext.AtrIndicators
            .Where(a => a.Symbol == symbol && a.CalculationVersion == CalculationVersion)
            .Select(a => a.BarDate)
            .ToListAsync(cancellationToken);
        var existingSet = existingKeys.ToHashSet();

        var barDates = bars.Select(b => b.Date).ToList();
        var dateToIndex = IndicatorWindowSpan.BuildDateIndex(barDates);
        var correctedDateSet = correctedCloseByDate.Keys.ToHashSet();

        var addedCount = 0;
        foreach (var annotated in annotatedResults)
        {
            var result = annotated.Result;
            if (existingSet.Contains(result.Date))
            {
                continue;
            }

            var span = IndicatorWindowSpan.GetWindowSpan(barDates, dateToIndex[result.Date], AtrGapChecker.WindowSize);
            var hasTiingoCorrectedClose = IndicatorWindowSpan.ContainsAny(span, correctedDateSet);

            _dbContext.AtrIndicators.Add(new AtrIndicator
            {
                Symbol = symbol,
                BarDate = result.Date,
                Period = result.Period,
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
            "[ATR]: {Symbol}: {BarCount} bar(s) evaluated, {AddedCount} new ATR row(s) added",
            symbol, bars.Count, addedCount);
    }

    // Narrow, evidence-scoped exception - see CLAUDE.md, "Reconciliation: Narrow Tiingo-Close
    // Exception for the OHL-Agrees/C-Disagrees Signature" (2026-07-17), and this session's
    // follow-up audit that found BarVerification.PrimarySourceValue had zero production
    // consumers despite being written whenever this exception fires. This closes that gap for
    // ATR specifically. NOT a general Alpaca-vs-Tiingo preference: only dates where
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
