using Drps.Calculator.Persistence;
using Drps.Calculator.Rsi;
using Microsoft.EntityFrameworkCore;

namespace Drps.Calculator.Verification;

/// <summary>
/// Live, read-time verification check for an RsiSlope value - same one-class-per-indicator
/// convention as DmaVerificationJoinService/RsiVerificationJoinService/RvolVerificationJoinService/
/// AtrVerificationJoinService. RsiSlope[t] = RSI[t] - RSI[t-Lookback] (RsiSlopeCalculator), so
/// its true dependency is TWO independent RSI endpoints, each already covered by
/// RsiVerificationJoinService.IsRsiVerifiedAsync's own 15-bar check - this class does not
/// re-implement that check, it composes it twice.
///
/// Per CLAUDE.md's "2026-08-04 — RsiSlope/RsiConcavity as Composite-Score Modifier, Not Tier 1
/// Gate": built as a prerequisite for letting RsiSlope/RsiConcavity influence any score at
/// all, even as a soft Adjuster modifier - an unverified signal must not move a score any more
/// than an unverified signal should gate Tier 1 eligibility. This task builds and tests the
/// primitive only; it is not yet called by GateQualityScorer, GateScanService, or
/// AdjusterScanService.
///
/// The "lookback" here is a ROW-POSITION offset in the symbol's own ordered RsiIndicator
/// sequence, NOT a calendar/trading-day offset - matching RsiSlopeCalculator/RsiSlopeGapChecker's
/// own semantics exactly (see RsiSlopeGapChecker's doc comment: "A slope value at index i
/// depends on the RSI series' own dates from index i-Lookback through i"). A gap in the RSI
/// series (a date RsiComputationService itself skipped) means "3 positions back" and "3 trading
/// days back" are not the same date - resolving the correct earlier endpoint requires reading
/// the real persisted row sequence, not subtracting calendar days from barDate.
/// </summary>
public class RsiSlopeVerificationJoinService
{
    private readonly CalculatorDbContext _dbContext;
    private readonly RsiVerificationJoinService _rsiVerificationJoinService;

    public RsiSlopeVerificationJoinService(
        CalculatorDbContext dbContext, RsiVerificationJoinService rsiVerificationJoinService)
    {
        _dbContext = dbContext;
        _rsiVerificationJoinService = rsiVerificationJoinService;
    }

    /// <summary>
    /// True only if BOTH RSI endpoints an RsiSlope value at <paramref name="barDate"/> was
    /// actually computed from - RSI[barDate] and RSI at the row <paramref name="lookback"/>
    /// positions earlier in this symbol's ordered RsiIndicator sequence - are independently
    /// verified per RsiVerificationJoinService.IsRsiVerifiedAsync's own 15-bar check.
    /// Fail-closed, same convention as every other verification-join service in this
    /// codebase: <paramref name="barDate"/> not being a real RsiIndicator row for this symbol,
    /// or there being fewer than <paramref name="lookback"/> + 1 RSI rows on or before it
    /// (insufficient history to resolve the earlier endpoint at all), both resolve to false
    /// rather than throwing or guessing. Short-circuits on the first false result to avoid an
    /// unnecessary second round of DB round-trips inside IsRsiVerifiedAsync.
    /// </summary>
    public async Task<bool> IsRsiSlopeVerifiedAsync(
        string symbol, DateOnly barDate, int lookback, CancellationToken cancellationToken)
    {
        // Descending, capped at lookback+1: the real persisted RsiIndicator row sequence
        // on or before barDate, current formula version only - same "only the latest
        // CalculationVersion is a live input" convention every cross-indicator read in this
        // codebase already follows (e.g. RsiSlopeComputationService's own RSI-series read).
        var recentRsiBarDates = await _dbContext.RsiIndicators
            .Where(r => r.Symbol == symbol
                && r.CalculationVersion == RsiComputationService.CalculationVersion
                && r.BarDate <= barDate)
            .OrderByDescending(r => r.BarDate)
            .Select(r => r.BarDate)
            .Take(lookback + 1)
            .ToListAsync(cancellationToken);

        if (recentRsiBarDates.Count < lookback + 1 || recentRsiBarDates[0] != barDate)
        {
            // Either fewer than lookback+1 RSI rows exist on or before barDate at all (the
            // earlier endpoint can't be resolved), or barDate itself isn't a real
            // RsiIndicator row for this symbol - fail closed rather than guess, same
            // "insufficient window depth" posture as DmaVerificationJoinService/
            // RsiVerificationJoinService's own bar-count guards.
            return false;
        }

        var isCurrentEndpointVerified = await _rsiVerificationJoinService.IsRsiVerifiedAsync(
            symbol, barDate, cancellationToken);
        if (!isCurrentEndpointVerified)
        {
            return false;
        }

        var earlierEndpointBarDate = recentRsiBarDates[^1];
        return await _rsiVerificationJoinService.IsRsiVerifiedAsync(
            symbol, earlierEndpointBarDate, cancellationToken);
    }
}
