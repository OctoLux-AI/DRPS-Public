using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Ingestion.Orchestration;

/// <summary>
/// Weekly Alpaca-vs-Tiingo data-quality audit (CLAUDE.md, "Weekly Data-Quality Audit: Alpaca
/// vs. Tiingo Variance", 2026-07-22). Distinct purpose from BarReconciliationService: that
/// service corrects individual bars at ingestion time against a fixed hybrid tolerance; this
/// service asks a slower-moving question the per-bar rule can't ask about itself - is the
/// pattern that justified the existing narrow OHL-agreed/Close-resolved-to-Tiingo exception (and
/// the tolerance itself) still true, or has it drifted since the original NVDA-split/basket-test
/// evidence was gathered?
///
/// Deliberately NOT wired to any pass/fail threshold or Pushover alert - CLAUDE.md is explicit
/// that no threshold is set yet, pending several weeks of real observed variance data. This
/// class only logs the raw distribution; a future task adds the threshold once that data exists.
/// </summary>
public class WeeklyVarianceAuditService
{
    private const string Resolution = "1Day";

    private readonly DrpsDbContext _dbContext;
    private readonly ILogger<WeeklyVarianceAuditService> _logger;

    public WeeklyVarianceAuditService(DrpsDbContext dbContext, ILogger<WeeklyVarianceAuditService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Compares every (ticker, trading date) pair inside the 7-calendar-day window ending on
    /// <paramref name="weekEndingDate"/> (inclusive) where BOTH Alpaca and Tiingo have a bar - a
    /// ticker with only one source all week simply never produces a row, no separate
    /// "eligible ticker" pre-filter needed. One WeeklyVarianceAuditEntry is written per field
    /// (Open/High/Low/Close) per matched (ticker, date) pair.
    /// </summary>
    public async Task RunAsync(DateOnly weekEndingDate, CancellationToken cancellationToken)
    {
        var weekStart = weekEndingDate.AddDays(-6);
        var startTimestamp = new DateTimeOffset(weekStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var endTimestamp = new DateTimeOffset(weekEndingDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var bars = await _dbContext.RawOhlcvBars
            .Where(b => b.Resolution == Resolution
                && (b.Source == SourceType.Alpaca || b.Source == SourceType.Tiingo)
                && b.Timestamp >= startTimestamp
                && b.Timestamp <= endTimestamp)
            .ToListAsync(cancellationToken);

        var comparedCount = 0;
        var entriesLoggedCount = 0;
        var singleSourceSkippedCount = 0;

        foreach (var group in bars.GroupBy(b => new { b.Symbol, b.Timestamp }))
        {
            // Raw bars are append-only: a source re-ingested across separate runs can have
            // multiple historical rows for the same (Symbol, Timestamp, Resolution). Only the
            // most recently ingested row per source is a live comparison candidate - same
            // dedup shape as BarReconciliationService.
            var bySource = group
                .GroupBy(b => b.Source)
                .Select(g => g.OrderByDescending(b => b.IngestedAt).ThenByDescending(b => b.Id).First())
                .ToDictionary(b => b.Source);

            if (!bySource.TryGetValue(SourceType.Alpaca, out var alpacaBar)
                || !bySource.TryGetValue(SourceType.Tiingo, out var tiingoBar))
            {
                singleSourceSkippedCount++;
                continue;
            }

            var ticker = group.Key.Symbol;
            var barDate = DateOnly.FromDateTime(group.Key.Timestamp.UtcDateTime);

            entriesLoggedCount += LogField(ticker, barDate, weekEndingDate, OhlcvField.Open, alpacaBar.Open, tiingoBar.Open);
            entriesLoggedCount += LogField(ticker, barDate, weekEndingDate, OhlcvField.High, alpacaBar.High, tiingoBar.High);
            entriesLoggedCount += LogField(ticker, barDate, weekEndingDate, OhlcvField.Low, alpacaBar.Low, tiingoBar.Low);
            entriesLoggedCount += LogField(ticker, barDate, weekEndingDate, OhlcvField.Close, alpacaBar.Close, tiingoBar.Close);

            comparedCount++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "[WEEKLY-VARIANCE-AUDIT]: week ending {WeekEndingDate}: {ComparedCount} ticker/date pair(s) with both " +
            "sources present, {EntriesLoggedCount} field variance entrie(s) logged, {SingleSourceSkippedCount} " +
            "ticker/date pair(s) skipped (single source only) - no threshold applied, raw distribution only",
            weekEndingDate, comparedCount, entriesLoggedCount, singleSourceSkippedCount);
    }

    /// <summary>
    /// Returns 1 if a row was logged, 0 if skipped (invalid Alpaca reference value). No
    /// threshold/pass-fail judgment of any kind - every valid comparison is logged, regardless
    /// of how large or small the variance is.
    /// </summary>
    private int LogField(string ticker, DateOnly barDate, DateOnly weekEndingDate, OhlcvField field, decimal alpacaValue, decimal tiingoValue)
    {
        // A real equity price is never zero or negative - same exclude-rather-than-compute-
        // garbage precedent as BarReconciliationService's Close<=0 guard. Guards PercentVariance
        // from dividing by zero; Tiingo's own value isn't checked since it's compared, not
        // divided by.
        if (alpacaValue <= 0m)
        {
            _logger.LogWarning(
                "[WEEKLY-VARIANCE-AUDIT]: excluding invalid Alpaca {Field} for {Ticker}/{BarDate}: {Value}",
                field, ticker, barDate, alpacaValue);
            return 0;
        }

        var absoluteVariance = Math.Abs(alpacaValue - tiingoValue);

        _dbContext.WeeklyVarianceAuditEntries.Add(new WeeklyVarianceAuditEntry
        {
            Ticker = ticker,
            BarDate = barDate,
            Field = field,
            AlpacaValue = alpacaValue,
            TiingoValue = tiingoValue,
            AbsoluteVariance = absoluteVariance,
            PercentVariance = absoluteVariance / alpacaValue,
            WeekEndingDate = weekEndingDate,
            DetectedAt = DateTimeOffset.UtcNow
        });

        return 1;
    }
}
