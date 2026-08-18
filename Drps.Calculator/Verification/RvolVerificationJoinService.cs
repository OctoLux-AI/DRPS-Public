using Drps.Calculator.Persistence;
using Drps.Calculator.Rvol;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Calculator.Verification;

/// <summary>
/// Live, read-time verification check for an RVOL value - deliberately not cached anywhere
/// (no Verified column on RvolIndicator), same discipline as DmaVerificationJoinService/
/// RsiVerificationJoinService. Every call re-derives the window's underlying bars and
/// re-queries BarVerification's current state, so the result always reflects "now."
/// </summary>
public class RvolVerificationJoinService
{
    private const string Resolution = "1Day";

    private readonly CalculatorDbContext _dbContext;

    public RvolVerificationJoinService(CalculatorDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// True only if every one of the RvolCalculator.WindowSize (21) bars an RVOL value at
    /// <paramref name="barDate"/> was actually computed from (20-bar baseline plus the
    /// current bar itself) is currently BarVerification.Verified == true. Fail-closed: one
    /// unverified bar (or one with no BarVerification row at all yet) makes the whole check
    /// unverified, not just the anchor bar.
    /// </summary>
    public async Task<bool> IsRvolVerifiedAsync(string symbol, DateOnly barDate, CancellationToken cancellationToken)
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

        // Same append-only dedup as RvolComputationService/DmaVerificationJoinService: only
        // the most recently ingested row per date is a live input.
        var windowDates = rawRows
            .GroupBy(r => r.Timestamp)
            .Select(g => g.OrderByDescending(r => r.IngestedAt).First())
            .OrderByDescending(r => r.Timestamp)
            .Take(RvolCalculator.WindowSize)
            .Select(r => r.Timestamp)
            .ToList();

        if (windowDates.Count < RvolCalculator.WindowSize)
        {
            // Fewer than WindowSize bars exist up to this date at all - this can't be the
            // window the RVOL value was actually computed from. Fail closed rather than
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
