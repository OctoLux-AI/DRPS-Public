using Drps.Calculator.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Calculator.Verification;

/// <summary>
/// Live, read-time verification check for a DMA window - deliberately not cached anywhere
/// (no Verified column on DmaIndicator). A DMA value computed today from currently-
/// unverified bars can become verified later once a second source catches up; a cached flag
/// would go stale the instant that happens. Every call re-derives the window's underlying
/// bars and re-queries BarVerification's current state, so the result always reflects "now."
/// </summary>
public class DmaVerificationJoinService
{
    private const string Resolution = "1Day";

    private readonly CalculatorDbContext _dbContext;

    public DmaVerificationJoinService(CalculatorDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// True only if every one of the <paramref name="window"/> bars a DMA-<paramref
    /// name="window"/> value at <paramref name="barDate"/> was actually computed from is
    /// currently BarVerification.Verified == true. Fail-closed: one unverified bar (or one
    /// with no BarVerification row at all yet) makes the whole window unverified, not just
    /// the anchor bar. The window is reconstructed by bar count (the most recent
    /// <paramref name="window"/> Alpaca bars up to and including barDate) rather than a
    /// calendar-date range, matching exactly how DmaCalculator.Compute selected window
    /// membership in the first place - a calendar-day range would misalign whenever a
    /// weekend/holiday falls inside the window.
    /// </summary>
    public async Task<bool> IsDmaVerifiedAsync(
        string symbol, DateOnly barDate, int window, CancellationToken cancellationToken)
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

        // Same append-only dedup as DmaComputationService: only the most recently ingested
        // row per date is a live input, so a re-ingested duplicate can't be double-counted
        // toward the window.
        var windowDates = rawRows
            .GroupBy(r => r.Timestamp)
            .Select(g => g.OrderByDescending(r => r.IngestedAt).First())
            .OrderByDescending(r => r.Timestamp)
            .Take(window)
            .Select(r => r.Timestamp)
            .ToList();

        if (windowDates.Count < window)
        {
            // Fewer than `window` bars exist up to this date at all - this can't be the
            // window the DMA value was actually computed from. Fail closed rather than
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
