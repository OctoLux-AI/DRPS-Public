using Drps.Calculator.Persistence;
using Drps.Calculator.Rsi;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Calculator.Verification;

/// <summary>
/// Live, read-time verification check for an RSI value - deliberately not cached anywhere
/// (no Verified column on RsiIndicator), same discipline as DmaVerificationJoinService. An
/// RSI value computed today from currently-unverified bars can become verified later once a
/// second source catches up; a cached flag would go stale the instant that happens. Every
/// call re-derives the window's underlying bars and re-queries BarVerification's current
/// state, so the result always reflects "now."
///
/// IMPORTANT SCOPE LIMIT (see RsiIndicator.VerificationScopeLimited): this check only
/// re-examines the most recent 15 bars (RsiGapChecker.WindowSize). Wilder's smoothing
/// recurrence means the RSI value at any given date actually carries a decaying-but-never-
/// zero contribution from every earlier bar in the symbol's history, all the way back to
/// its first computed value - this method does not, and is not intended to, re-verify that
/// entire chain. A true result here means "the recent 15-bar window this practically
/// depends on is verified," not "every bar that has ever influenced this number is
/// verified." Full-history verification is a deliberate non-goal, not an oversight.
/// </summary>
public class RsiVerificationJoinService
{
    private const string Resolution = "1Day";

    private readonly CalculatorDbContext _dbContext;

    public RsiVerificationJoinService(CalculatorDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// True only if every one of the RsiGapChecker.WindowSize (15) bars an RSI-14 value at
    /// <paramref name="barDate"/> was actually computed from is currently
    /// BarVerification.Verified == true. Fail-closed: one unverified bar (or one with no
    /// BarVerification row at all yet) makes the whole check unverified, not just the
    /// anchor bar. 15 (not 14) matches the true minimum data dependency of Wilder's seed
    /// average - see RsiCalculator/RsiGapChecker's own doc comments for the full reasoning,
    /// including the stated simplification for bars beyond the first seeded value.
    /// </summary>
    public async Task<bool> IsRsiVerifiedAsync(string symbol, DateOnly barDate, CancellationToken cancellationToken)
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

        // Same append-only dedup as RsiComputationService/DmaVerificationJoinService: only
        // the most recently ingested row per date is a live input.
        var windowDates = rawRows
            .GroupBy(r => r.Timestamp)
            .Select(g => g.OrderByDescending(r => r.IngestedAt).First())
            .OrderByDescending(r => r.Timestamp)
            .Take(RsiGapChecker.WindowSize)
            .Select(r => r.Timestamp)
            .ToList();

        if (windowDates.Count < RsiGapChecker.WindowSize)
        {
            // Fewer than WindowSize bars exist up to this date at all - this can't be the
            // window the RSI value was actually computed from. Fail closed rather than
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
