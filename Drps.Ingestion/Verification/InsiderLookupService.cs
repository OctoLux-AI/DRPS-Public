using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Ingestion.Verification;

/// <summary>
/// Live, read-time lookup for a ticker's insider Form 4 purchase multiplier (Insider Form 4
/// Adjuster Multiplier Decision, CLAUDE.md 2026-07-19) - Adjuster's own sizing-only read
/// path, same shape as SectorLookupService/EarningsLookupService (constructor-injected
/// DbContext, no caching, asOf supplied by the caller rather than an internally-held clock).
/// Placed here rather than in Drps.Adjuster for the same reason those two are -
/// RawInsiderObservation is a DrpsDbContext table (Drps.Ingestion-owned).
///
/// Deliberately never blocks anything - unlike EarningsLookupService's hard BUY/no-BUY gate,
/// every failure/no-signal mode here (missing volume history, a ticker never scanned by
/// EdgarForm4Feeder, or a ticker scanned with zero purchases found) collapses to the same
/// neutral 1.0x multiplier, per the decision block's own "upgrade-only, never penalizes"
/// scope. DMA remains the sole binary eligibility gate; this multiplier only ever adjusts
/// sizing for a candidate that already cleared Gate. IsDataUnverified distinguishes the
/// first two (a genuine data gap) from the third (a real, checked, clean answer) - see
/// GetMultiplierAsync's own comments for exactly which case is which.
/// </summary>
public class InsiderLookupService
{
    // Insider Form 4 Adjuster Multiplier Decision's stated 60-day trailing window - Kent's
    // correction from CapitalFill's 90-day precedent. Explicitly an uncalibrated placeholder,
    // same category as ATR-multiple/cooldown-scaling placeholders elsewhere in CLAUDE.md -
    // needs real trade data to tune, not a guessed final value.
    private const int WindowDays = 60;

    // Matches RvolCalculator.BaselineWindow (Drps.Calculator) - duplicated as a literal here
    // since Drps.Ingestion has no project reference to Drps.Calculator (RVOL's own
    // computation depends on Ingestion's raw bars, not the other way around - the dependency
    // only runs one direction). This is still "reusing the existing RVOL input" per this
    // task's own instruction, not a new volume source: it reads the identical RawOhlcvBars
    // table over the identical trailing-20-day window RVOL's own calculation already
    // establishes, just averaged in dollars (Volume x Close) rather than expressed as
    // today's-bar-over-baseline ratio. Keep in sync if RVOL's baseline window ever changes.
    private const int DollarVolumeWindowDays = 20;

    // [REDACTED FOR PUBLIC RELEASE] Real calibrated values, not included in this public
    // repository - see README.md's "What's intentionally not public" section.
    public const decimal MultiplierCap = 0m;
    public const decimal ThresholdRatio = 0m;

    private readonly DrpsDbContext _dbContext;

    public InsiderLookupService(DrpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // `asOf` is supplied by the caller, never read from an internally-held clock - matches
    // SectorLookupService/EarningsLookupService's identical clock-injection discipline.
    public async Task<InsiderMultiplierResult> GetMultiplierAsync(string ticker, DateTime asOf, CancellationToken cancellationToken)
    {
        var asOfUtc = asOf.Kind == DateTimeKind.Utc ? asOf : asOf.ToUniversalTime();
        var asOfDate = DateOnly.FromDateTime(asOfUtc);

        // A genuine "cannot compute" case - no ratio can be formed without a real dollar-
        // volume denominator. Distinct from "zero insider purchases found" below, which is a
        // complete, trustworthy answer (checked, found nothing), not a data gap.
        var avgDollarVolume = await GetAverageDailyDollarVolumeAsync(ticker, asOfDate, cancellationToken);
        if (avgDollarVolume is null || avgDollarVolume.Value <= 0m)
        {
            return new InsiderMultiplierResult { Multiplier = 1.0m, IsDataUnverified = true };
        }

        // "No successful scan on record" - a real, unambiguous data gap, distinguishable from
        // "scanned, found nothing" now that EdgarForm4Feeder always leaves a real
        // Verified=true marker row behind for a genuinely clean window (see that class's own
        // doc comment). Deliberately filtered to Verified=true here, not just "any row at
        // all": a ticker whose only RawInsiderObservation history is a failed-scan sentinel
        // (Verified=false, EdgarForm4Feeder's own fail-closed row) has never actually been
        // successfully checked - a failed attempt is not evidence anything was verified, so
        // it must read the same as a ticker with zero rows entirely, not as "scanned clean."
        var hasVerifiedObservation = await _dbContext.RawInsiderObservations
            .AnyAsync(o => o.Ticker == ticker && o.Source == SourceType.SecEdgarForm4 && o.Verified, cancellationToken);

        if (!hasVerifiedObservation)
        {
            return new InsiderMultiplierResult { Multiplier = 1.0m, IsDataUnverified = true };
        }

        var windowStart = asOfDate.AddDays(-WindowDays);
        var summedDollarValue = await _dbContext.RawInsiderObservations
            .Where(o => o.Ticker == ticker && o.Source == SourceType.SecEdgarForm4 && o.Verified
                && o.TransactionDate >= windowStart && o.TransactionDate <= asOfDate)
            .SumAsync(o => (decimal?)o.DollarValue, cancellationToken) ?? 0m;

        if (summedDollarValue <= 0m)
        {
            // Missing/zero-observation case (Insider Form 4 Adjuster Multiplier Decision,
            // point 3) - neutral, and now genuinely NOT flagged unverified:
            // hasVerifiedObservation above already confirmed a real, successful scan
            // happened for this ticker at some point (most commonly EdgarForm4Feeder's own
            // Verified=true, zero-value "scanned clean" marker row), so this is a
            // trustworthy "checked, nothing found" answer, not a guess.
            return new InsiderMultiplierResult { Multiplier = 1.0m, IsDataUnverified = false };
        }

        var ratio = summedDollarValue / avgDollarVolume.Value;
        return new InsiderMultiplierResult { Multiplier = ComputeMultiplier(ratio), IsDataUnverified = false };
    }

    // [REDACTED FOR PUBLIC RELEASE]
    // Maps a dollar-weighted insider-purchase ratio to a sizing multiplier, clamped to an
    // independent cap before it ever touches the Kelly multiplier. The exact curve shape and
    // cap are proprietary and not included in this public repository - see README.md's "What's
    // intentionally not public" section.
    public static decimal ComputeMultiplier(decimal ratio)
    {
        throw new NotImplementedException("Redacted for public release - see README.md");
    }

    // Average(Volume x Close) over the trailing DollarVolumeWindowDays raw daily bars ending
    // at/before asOfDate - the same Alpaca-primary RawOhlcvBars source and "<= cutoff"
    // look-ahead-bias guard GateScanService/AdjusterScanService already use for price
    // lookups. Null when fewer than a full window of bars exists (e.g. a recently-added
    // ticker) - a real data gap, not silently averaged over a partial/misleading window.
    private async Task<decimal?> GetAverageDailyDollarVolumeAsync(string ticker, DateOnly asOfDate, CancellationToken cancellationToken)
    {
        var cutoff = new DateTimeOffset(asOfDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var bars = await _dbContext.RawOhlcvBars
            .Where(b => b.Symbol == ticker && b.Source == SourceType.Alpaca && b.Resolution == "1Day" && b.Timestamp <= cutoff)
            .OrderByDescending(b => b.Timestamp)
            .Take(DollarVolumeWindowDays)
            .ToListAsync(cancellationToken);

        if (bars.Count < DollarVolumeWindowDays)
        {
            return null;
        }

        return bars.Average(b => b.Volume * b.Close);
    }
}
