using Drps.Ingestion.Persistence;
using Drps.Shared.Positioning;
using Drps.Shared.Scoring;

namespace Drps.Ledger;

/// <summary>
/// Real, Drps.Ledger-backed implementation of IPositionStateProvider - reads actual Position
/// rows rather than StubPositionStateProvider's always-false placeholder. Wired into Drps.Gate's
/// DI registration (Drps.Gate/Program.cs) as the active IPositionStateProvider;
/// StubPositionStateProvider remains in Drps.Gate.Positioning as a reference/fallback only.
///
/// Matches IPositionStateProvider's actual signature exactly (synchronous, no asOf
/// parameter) - neither method genuinely needs a "current time" input: IsCurrentlyHeld is a
/// live ExitDate-is-null check, and GetNoBuyExpiration's returned date is a deterministic
/// function of the stored CoolDownStartDate alone (whether that expiration has already passed
/// is GateCompositeService's own asOf-aware comparison in Score(), not this provider's job).
/// No DateTime.Now call anywhere in this class as a result.
/// </summary>
public class LedgerPositionStateProvider : IPositionStateProvider
{
    private readonly DrpsDbContext _dbContext;

    public LedgerPositionStateProvider(DrpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public bool IsCurrentlyHeld(string ticker) =>
        _dbContext.Positions.Any(p => p.Ticker == ticker && p.ExitDate == null);

    // Most recent Position row (by CoolDownStartDate) for this ticker that actually carries a
    // cooldown stamp - a ticker can accumulate several closed positions over time, only the
    // latest trigger is relevant. Reuses NoBuyExpirationCalculator (extracted from
    // GateCompositeService) so this computes the identical 2-full-trading-session rule rather
    // than a second, potentially-drifting copy of it. Always returns the raw computed
    // expiration - including one already in the past - matching GateCompositeService's own
    // behavior exactly; null is reserved for "no cooldown was ever triggered."
    public DateTime? GetNoBuyExpiration(string ticker)
    {
        var coolDownStartDate = _dbContext.Positions
            .Where(p => p.Ticker == ticker && p.CoolDownStartDate != null)
            .OrderByDescending(p => p.CoolDownStartDate)
            .Select(p => p.CoolDownStartDate)
            .FirstOrDefault();

        if (coolDownStartDate is null)
        {
            return null;
        }

        // Fail-closed, same precedent as GateScanService's own active-row check - without a
        // real NoBuySessionCount this cannot be answered correctly, and silently guessing
        // risks either wrongly blocking or wrongly permitting a BUY.
        var activeParametersRows = _dbContext.GateParameters.Where(p => p.IsActive).ToList();
        if (activeParametersRows.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one active GateParameters row to compute a NoBuy expiration for {ticker}, found {activeParametersRows.Count}.");
        }

        return NoBuyExpirationCalculator.ComputeExpiration(coolDownStartDate.Value, activeParametersRows[0].NoBuySessionCount);
    }

    // Read-through for ShareCapDeficient - Position no longer carries its own
    // independently-written copy of this flag (consolidated per CLAUDE.md's 2026-07-18
    // decision block, point 4, closing undocumented drift between Position.ShareCapDeficient
    // and AdjusterAllocation.ShareCapDeficient found during this session's audit).
    // AdjusterAllocation.ShareCapDeficient is the single real, wired value, set at sizing time
    // (AdjusterSizingService); this joins through Position's existing AdjusterAllocationId FK
    // via an explicit query rather than a navigation property, matching
    // PositionConfiguration's "FK references only, no navigation properties either side"
    // convention. Returns null only when positionId doesn't resolve to a real Position -
    // AdjusterAllocationId is a required FK validated at Position-creation time
    // (ManualPositionEntryService.OpenPositionAsync), so a resolved Position always has a
    // real AdjusterAllocation to read through.
    public bool? GetShareCapDeficient(long positionId)
    {
        return _dbContext.Positions
            .Where(p => p.Id == positionId)
            .Join(_dbContext.AdjusterAllocations,
                p => p.AdjusterAllocationId,
                a => a.Id,
                (p, a) => (bool?)a.ShareCapDeficient)
            .FirstOrDefault();
    }
}
