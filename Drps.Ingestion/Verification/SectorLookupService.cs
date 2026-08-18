using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Ingestion.Verification;

/// <summary>
/// Live, read-time lookup for a ticker's sector/industry classification - the read path
/// Gate uses to resolve GateScore.Sector. Same shape as Drps.Calculator's
/// *VerificationJoinService classes (a single constructor-injected DbContext, no caching),
/// placed here rather than there because RawSectorObservation is a DrpsDbContext table
/// (Drps.Ingestion-owned), not a CalculatorDbContext one.
///
/// Only ever reads Finnhub-sourced, Verified rows - SecEdgar rows are never read by this
/// method, informational only per the decision to add SEC EDGAR as a second source (see
/// RawSectorObservation's own doc comment: the two sources use incompatible taxonomies and
/// are never compared).
/// </summary>
public class SectorLookupService
{
    // Same 7-day staleness TTL CLAUDE.md's Value + Provenance pattern already establishes
    // for slow-moving reference data (52-week high/low, shares outstanding, market cap) -
    // applied identically here, not a new/separate threshold.
    private static readonly TimeSpan StalenessTtl = TimeSpan.FromDays(7);

    private readonly DrpsDbContext _dbContext;

    public SectorLookupService(DrpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // `asOf` is supplied by the caller, never read from an internally-held clock - matches
    // Gate's existing clock-injection discipline (GateScanService.RunScanAsync's own doc
    // comment). Fail-closed: no matching row, or a row older than the TTL, both return null
    // rather than a stale/guessed value - the same honest "cannot verify" signal
    // GateScore.Sector's nullable-by-design comment already describes.
    public async Task<string?> GetSectorAsync(string ticker, DateTime asOf, CancellationToken cancellationToken)
    {
        var mostRecent = await _dbContext.RawSectorObservations
            .Where(o => o.Ticker == ticker && o.Source == SectorSourceType.Finnhub && o.Verified)
            .OrderByDescending(o => o.FetchedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (mostRecent is null)
            return null;

        // RawSectorObservation.FetchedAt is always written as DateTime.UtcNow (every sector
        // feeder), but asOf's Kind depends on its caller - Drps.Gate's Worker currently
        // defaults its own clock seam to local DateTime.Now (same default ExDividendWorker/
        // SectorWorker already use). Normalizing here, rather than trusting the caller to
        // have already done so, keeps this comparison correct regardless of which Kind asOf
        // arrives as - a naive subtraction of a local asOf against a UTC FetchedAt would
        // silently skew the TTL check by the local UTC offset.
        var asOfUtc = asOf.Kind == DateTimeKind.Utc ? asOf : asOf.ToUniversalTime();

        if (asOfUtc - mostRecent.FetchedAt > StalenessTtl)
            return null;

        return mostRecent.SectorValue;
    }
}
