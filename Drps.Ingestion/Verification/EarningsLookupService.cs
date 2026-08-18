using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Ingestion.Verification;

/// <summary>
/// Live, read-time lookup for a ticker's earnings-blackout status - the read path Gate's
/// hard gate uses (Earnings Blackout Gate Decision, CLAUDE.md 2026-07-19). Same shape as
/// SectorLookupService: a single constructor-injected DbContext, no caching, asOf supplied
/// by the caller rather than an internally-held clock. Placed here rather than in Drps.Gate
/// for the same reason SectorLookupService is - RawEarningsObservation is a DrpsDbContext
/// table (Drps.Ingestion-owned).
///
/// Only ever reads Finnhub-sourced rows - the only earnings source that exists today.
/// </summary>
public class EarningsLookupService
{
    // Same 7-day staleness TTL CLAUDE.md's Value + Provenance pattern already establishes
    // for slow-moving reference data, applied identically here - matches
    // RawEarningsObservation's own doc comment and SectorLookupService's identical constant.
    private static readonly TimeSpan StalenessTtl = TimeSpan.FromDays(7);

    // Earnings Blackout Gate Decision's stated window: a next earnings date within 48 hours
    // of scan time excludes the candidate from BUY.
    private const int BlackoutWindowHours = 48;

    private readonly DrpsDbContext _dbContext;

    public EarningsLookupService(DrpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // `asOf` is supplied by the caller, never read from an internally-held clock - matches
    // SectorLookupService's and Gate's own clock-injection discipline.
    //
    // Three-way mapping from EarningsFetchOutcome (CLAUDE.md's "Earnings Verification
    // Tri-State Fix," 2026-08-07), replacing the old bool-based check that treated
    // "genuinely nothing upcoming" and "couldn't parse the response" identically:
    //   - Unknown, or no row at all -> Unverified (fail-closed, unchanged from before).
    //   - NoUpcomingEarningsInWindow -> Clear (the actual bug fix - this used to be
    //     Unverified too, silently capping every such candidate at WATCH).
    //   - UpcomingEarningsFound -> BlackoutActive or Clear, per the existing 48-hour
    //     threshold math below, unchanged.
    public async Task<EarningsBlackoutStatus> GetBlackoutStatusAsync(string ticker, DateTime asOf, CancellationToken cancellationToken)
    {
        var mostRecent = await _dbContext.RawEarningsObservations
            .Where(o => o.Ticker == ticker && o.Source == SourceType.Finnhub)
            .OrderByDescending(o => o.FetchedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (mostRecent is null || mostRecent.FetchOutcome == EarningsFetchOutcome.Unknown)
        {
            return EarningsBlackoutStatus.Unverified;
        }

        // RawEarningsObservation.FetchedAt is always written as DateTime.UtcNow (the only
        // feeder that exists), but asOf's Kind depends on its caller - same normalization
        // SectorLookupService applies, for the same reason (a naive subtraction against a
        // non-UTC asOf would silently skew the TTL comparison by the local UTC offset).
        // Applies uniformly to both remaining outcomes below - a stale row is unverified
        // regardless of what it last reported.
        var asOfUtc = asOf.Kind == DateTimeKind.Utc ? asOf : asOf.ToUniversalTime();

        if (asOfUtc - mostRecent.FetchedAt > StalenessTtl)
        {
            return EarningsBlackoutStatus.Unverified;
        }

        if (mostRecent.FetchOutcome == EarningsFetchOutcome.NoUpcomingEarningsInWindow)
        {
            return EarningsBlackoutStatus.Clear;
        }

        // FetchOutcome == UpcomingEarningsFound here. Defensively guarded rather than
        // assumed - FinnhubEarningsFeeder never actually produces UpcomingEarningsFound
        // without a date, but failing closed here rather than trusting that invariant holds
        // forever is the same discipline this codebase applies elsewhere (e.g.
        // GateScanService's own defensive null-guard after candidate discovery).
        if (mostRecent.NextEarningsDate is null)
        {
            return EarningsBlackoutStatus.Unverified;
        }

        var earningsMidnightUtc = mostRecent.NextEarningsDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var hoursUntilEarnings = (earningsMidnightUtc - asOfUtc).TotalHours;

        // Only an upcoming (not already-elapsed) date within the window counts as active -
        // a date that has already passed relative to asOf no longer represents an imminent
        // event to avoid trading around, regardless of how the row got into that state.
        return hoursUntilEarnings >= 0 && hoursUntilEarnings <= BlackoutWindowHours
            ? EarningsBlackoutStatus.BlackoutActive
            : EarningsBlackoutStatus.Clear;
    }
}
