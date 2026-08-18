using Drps.Calculator.Atr;
using Drps.Calculator.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Calculator.Verification;

/// <summary>
/// Live, read-time verification check for an ATR value - deliberately not cached anywhere
/// (no Verified column on AtrIndicator), same discipline as DmaVerificationJoinService/
/// RsiVerificationJoinService/RvolVerificationJoinService. Every call re-derives the
/// window's underlying bars and re-queries BarVerification's current state, so the result
/// always reflects "now."
///
/// IMPORTANT SCOPE LIMIT (see AtrIndicator.VerificationScopeLimited): this check only
/// re-examines the most recent 15 bars (AtrGapChecker.WindowSize). Wilder's smoothing
/// recurrence means the ATR value at any given date actually carries a decaying-but-never-
/// zero contribution from every earlier bar in the symbol's history, all the way back to
/// its first computed value - this method does not, and is not intended to, re-verify that
/// entire chain. A true result here means "the recent 15-bar window this practically
/// depends on is verified," not "every bar that has ever influenced this number is
/// verified." Full-history verification is a deliberate non-goal, not an oversight.
/// </summary>
public class AtrVerificationJoinService
{
    private const string Resolution = "1Day";

    private readonly CalculatorDbContext _dbContext;

    public AtrVerificationJoinService(CalculatorDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// True only if every one of the AtrGapChecker.WindowSize (15) bars an ATR-14 value at
    /// <paramref name="barDate"/> was actually computed from is currently
    /// BarVerification.Verified == true. Fail-closed: one unverified bar (or one with no
    /// BarVerification row at all yet) makes the whole check unverified, not just the
    /// anchor bar. 15 (not 14) matches the true minimum data dependency of Wilder's seed
    /// average - see AtrCalculator/AtrGapChecker's own doc comments for the full reasoning.
    /// </summary>
    public async Task<bool> IsAtrVerifiedAsync(string symbol, DateOnly barDate, CancellationToken cancellationToken)
    {
        var barDateTimestamp = new DateTimeOffset(barDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var rawRows = await _dbContext.RawOhlcvBars
            .Where(b => b.Symbol == symbol
                && b.Resolution == Resolution
                && b.Source == SourceType.Alpaca
                && b.Timestamp <= barDateTimestamp)
            .OrderByDescending(b => b.Timestamp)
            .Select(b => new { b.Timestamp, b.IngestedAt })
            .ToListAsync(cancellationToken);

        // Same append-only dedup as AtrComputationService/DmaVerificationJoinService: only
        // the most recently ingested row per date is a live input.
        var windowDates = rawRows
            .GroupBy(r => r.Timestamp)
            .Select(g => g.OrderByDescending(r => r.IngestedAt).First())
            .OrderByDescending(r => r.Timestamp)
            .Take(AtrGapChecker.WindowSize)
            .Select(r => r.Timestamp)
            .ToList();

        if (windowDates.Count < AtrGapChecker.WindowSize)
        {
            // Fewer than WindowSize bars exist up to this date at all - this can't be the
            // window the ATR value was actually computed from. Fail closed rather than
            // report verified.
            return false;
        }

        var verifications = await _dbContext.BarVerifications
            .Where(v => v.Symbol == symbol
                && v.Resolution == Resolution
                && windowDates.Contains(v.Timestamp))
            .ToListAsync(cancellationToken);

        foreach (var date in windowDates)
        {
            var verification = verifications.FirstOrDefault(v => v.Timestamp == date);
            if (verification is null || !verification.Verified)
            {
                return false;
            }
        }

        return true;
    }
}
