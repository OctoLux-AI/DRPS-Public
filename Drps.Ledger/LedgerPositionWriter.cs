using Drps.Ingestion.Persistence;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Ledger;

/// <summary>
/// The actual Position field-setting logic and its validation - extracted from
/// ManualPositionEntryService per CLAUDE.md's Execution Layer: Fourth Design Decision, which
/// calls for one shared, audited write path that both the manual CLI and a future automated
/// execution layer call identically, rather than each maintaining its own copy of
/// position-creation/closing validation (the same category of risk that caused the earlier
/// two-field ShareCapDeficient drift between Position and AdjusterAllocation).
///
/// Every validation/invariant from the pre-refactor ManualPositionEntryService is preserved
/// unchanged: GateScoreId/AdjusterAllocationId existence checks on open, the already-closed
/// (ExitDate not null) guard on close, and the exact same field set written in each case.
/// ManualPositionEntryService itself is a thin wrapper delegating here - see that class's own
/// doc comment.
///
/// Hardened (this task) against three gaps noted when this class was first extracted:
/// positive-value validation (InvalidPositionValueException, checked before any row is
/// touched), a genuine concurrent-close race (Position.RowVersion concurrency token +
/// PositionAlreadyModifiedException, translated from EF's own DbUpdateConcurrencyException),
/// and a belt-and-suspenders duplicate-open guard (a filtered unique index on Ticker WHERE
/// ExitDate IS NULL + DuplicateOpenPositionException, translated from EF's own
/// DbUpdateException) - OpenCandidateQuery's own idempotency check already prevents this in
/// the normal path; this is the database itself refusing even if that upstream check is ever
/// bypassed or buggy.
///
/// Lives in Drps.Ledger (not Drps.Shared) because it needs direct DrpsDbContext access -
/// Drps.Shared stays persistence-agnostic (zero EF Core references), same reasoning already
/// documented on DrpsDbContext's own persistence-layer notes. This task deliberately does not
/// add a Drps.Execution reference to this class or Drps.Ledger - no automated caller exists
/// yet; that wiring is separate, later work.
/// </summary>
public class LedgerPositionWriter
{
    // Unvalidated placeholder (CLAUDE.md's Execution Layer: Consecutive-Loss Circuit Breaker
    // Design Decision, point 2) - same category as PreFireGateSettings.KillSwitchMaxOpensPerDay,
    // borrowed directly from CapitalFill's own number, not calibrated against any DRPS trade
    // data. A plain const, not an IOptions-bound setting - Drps.Ledger deliberately has no
    // config-binding/DI-host ceremony (see this project's own Program.cs doc comment), same
    // precedent as FillConfirmationService.TpTargetAtrMultiple.
    private const int ConsecutiveLossThreshold = 3;

    private readonly DrpsDbContext _dbContext;
    private readonly Func<CancellationToken, Task> _saveChangesAsync;

    public LedgerPositionWriter(DrpsDbContext dbContext, Func<CancellationToken, Task>? saveChangesAsync = null)
    {
        _dbContext = dbContext;
        // Injectable so a test can force a specific DbUpdateException/DbUpdateConcurrencyException
        // deterministically - same seam pattern as OrderFiringService's injectable delay. Needed
        // because EF Core's InMemory provider (confirmed empirically) does not enforce unique
        // indexes at all, filtered or unfiltered, in this EF Core version - the filtered unique
        // index this class's DuplicateOpenPositionException path exists to translate can never
        // fire naturally under this test suite's provider. Production code always uses the real
        // default (a direct SaveChangesAsync call against the real DbContext).
        _saveChangesAsync = saveChangesAsync ?? (ct => _dbContext.SaveChangesAsync(ct));
    }

    public async Task<Position> OpenPositionAsync(
        long gateScoreId,
        long adjusterAllocationId,
        string ticker,
        DateTime entryDate,
        decimal entryPrice,
        decimal entryQuantity,
        CancellationToken cancellationToken,
        PositionActionOrigin openOrigin,
        decimal? entryAtr = null,
        decimal? tpTargetPrice = null,
        decimal? highWaterMark = null)
    {
        if (entryPrice <= 0m)
        {
            throw new InvalidPositionValueException(nameof(entryPrice), entryPrice);
        }

        if (entryQuantity <= 0m)
        {
            throw new InvalidPositionValueException(nameof(entryQuantity), entryQuantity);
        }

        var gateScoreExists = await _dbContext.GateScores
            .AnyAsync(g => g.Id == gateScoreId, cancellationToken);
        if (!gateScoreExists)
        {
            throw new InvalidOperationException(
                $"GateScore {gateScoreId} does not exist - refusing to open a position against a nonexistent scoring decision.");
        }

        var adjusterAllocationExists = await _dbContext.AdjusterAllocations
            .AnyAsync(a => a.Id == adjusterAllocationId, cancellationToken);
        if (!adjusterAllocationExists)
        {
            throw new InvalidOperationException(
                $"AdjusterAllocation {adjusterAllocationId} does not exist - refusing to open a position against a nonexistent sizing decision.");
        }

        var position = new Position
        {
            Ticker = ticker,
            GateScoreId = gateScoreId,
            AdjusterAllocationId = adjusterAllocationId,
            EntryDate = entryDate,
            EntryPrice = entryPrice,
            EntryQuantity = entryQuantity,
            // CLAUDE.md's Execution Layer: Tenth Design Decision - required, not optional; the
            // caller always states its origin explicitly (ManualPositionEntryService hardcodes
            // Manual, FillConfirmationService/ReconciliationService hardcode Automated).
            OpenOrigin = openOrigin,
            // Entry is the natural starting plateau baseline (2026-07-18 decision block, point
            // 3 describes resets "on trigger" but never an initial value - entry is the only
            // reference point that exists before any reassessment has ever fired).
            PlateauBaselinePrice = entryPrice,
            PlateauBaselineDate = entryDate,
            // All three null for a manual-entry open (no automated fill/GateScore.AtrValue
            // link) - FillConfirmationService is the only caller that supplies real values here.
            EntryAtr = entryAtr,
            TpTargetPrice = tpTargetPrice,
            HighWaterMark = highWaterMark
        };

        _dbContext.Positions.Add(position);

        try
        {
            await _saveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // The only realistic failure mode at this point (both FKs already confirmed to
            // exist above) is the filtered unique index on (Ticker, WHERE ExitDate IS NULL) -
            // belt-and-suspenders against OpenCandidateQuery's own idempotency check ever being
            // bypassed or buggy. Translated to a specific, Ledger-meaningful exception rather
            // than letting EF's generic DbUpdateException leak past this write path.
            throw new DuplicateOpenPositionException(ticker, ex);
        }

        return position;
    }

    public async Task ClosePositionAsync(
        long positionId,
        DateTime exitDate,
        decimal exitPrice,
        decimal exitQuantity,
        PositionExitReason exitReason,
        CancellationToken cancellationToken,
        PositionActionOrigin closeOrigin,
        decimal? tpProgressAtExit = null)
    {
        if (exitPrice <= 0m)
        {
            throw new InvalidPositionValueException(nameof(exitPrice), exitPrice);
        }

        if (exitQuantity <= 0m)
        {
            throw new InvalidPositionValueException(nameof(exitQuantity), exitQuantity);
        }

        var position = await _dbContext.Positions
            .FirstOrDefaultAsync(p => p.Id == positionId, cancellationToken);
        if (position is null)
        {
            throw new InvalidOperationException(
                $"Position {positionId} does not exist.");
        }

        if (position.ExitDate is not null)
        {
            throw new InvalidOperationException(
                $"Position {positionId} was already closed on {position.ExitDate:O} - refusing to close it again.");
        }

        position.ExitDate = exitDate;
        position.ExitPrice = exitPrice;
        position.ExitQuantity = exitQuantity;
        position.ExitReason = exitReason;
        // Null for every ExitReason other than AtrStop - only AtrRatchetMonitorWorker's breach
        // path supplies a real value (CLAUDE.md's Execution Layer: Third Design Decision,
        // "Grading distinction, without forking behavior").
        position.TpProgressAtExit = tpProgressAtExit;
        // CLAUDE.md's Execution Layer: Tenth Design Decision - required, not optional; the
        // caller always states its origin explicitly (ManualPositionEntryService hardcodes
        // Manual, FillConfirmationService/ReconciliationService hardcode Automated).
        position.CloseOrigin = closeOrigin;

        // CLAUDE.md's Execution Layer: Consecutive-Loss Circuit Breaker Design Decision (point 5)
        // as refined by the Tenth Design Decision - the increment/trip bookkeeping lives here, in
        // the same SaveChangesAsync call that writes Position's own Exit* fields, so every
        // AUTOMATED close (FillConfirmationService, ReconciliationService) updates it exactly
        // once. A MANUAL close (Drps.Ledger's own CLI) reaches this same method but
        // UpdateCircuitBreakerAsync itself now filters it out entirely - see that method's own
        // doc comment for why.
        await UpdateCircuitBreakerAsync(position, exitPrice, cancellationToken);

        try
        {
            await _saveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Position.RowVersion (PositionConfiguration.IsRowVersion()) fires this when
            // another writer closed (or otherwise updated) this same row between our load
            // above and this save - translated to a specific, Ledger-meaningful exception
            // rather than letting EF's raw concurrency exception leak past this write path.
            throw new PositionAlreadyModifiedException(positionId, ex);
        }
    }

    /// <summary>
    /// CLAUDE.md's Execution Layer: Consecutive-Loss Circuit Breaker Design Decision, points 1
    /// and 6, as refined by the Tenth Design Decision's origin-scoping. Single tracker row,
    /// found-or-created on first use. A loss is ExitPrice &lt; EntryPrice (sign only, no
    /// magnitude floor - point 1); every PositionExitReason counts identically. A win or
    /// breakeven close resets the streak to zero. Once ConsecutiveLossThreshold is reached,
    /// Tripped stays true regardless of what happens afterward - it is cleared only by the
    /// manual reset-circuit-breaker CLI action, never by this method (point 4).
    ///
    /// Filtered to CloseOrigin == Automated only (Tenth Design Decision) - a manually-closed
    /// position (win or loss) neither increments, resets, nor otherwise touches this tracker.
    /// The breaker exists to answer "is the strategy itself failing right now," which is a
    /// question about the automated system's real, live decisions - not about every reason a
    /// Position might ever get closed (Kent's own parallel manual trading, test/cleanup closes
    /// against real LocalDB, or a manual emergency close made specifically because something
    /// already looks wrong). Counting any of those would let noise unrelated to the automated
    /// system's actual performance silently trip - or silently forestall tripping - a safety
    /// mechanism meant to watch that system specifically.
    /// </summary>
    private async Task UpdateCircuitBreakerAsync(Position position, decimal exitPrice, CancellationToken cancellationToken)
    {
        if (position.CloseOrigin != PositionActionOrigin.Automated)
        {
            return;
        }

        var breaker = await _dbContext.ConsecutiveLossCircuitBreakers.SingleOrDefaultAsync(cancellationToken);
        if (breaker is null)
        {
            breaker = new ConsecutiveLossCircuitBreaker();
            _dbContext.ConsecutiveLossCircuitBreakers.Add(breaker);
        }

        // Guards against double-counting the same close (point 6) - should not happen in
        // practice (ClosePositionAsync already refuses an already-closed Position above), but
        // this is the specific, named guard the design decision calls for.
        if (breaker.LastEvaluatedPositionId == position.Id)
        {
            return;
        }

        var isLoss = exitPrice < position.EntryPrice;

        if (isLoss)
        {
            breaker.ConsecutiveLossCount++;

            if (breaker.ConsecutiveLossCount >= ConsecutiveLossThreshold && !breaker.Tripped)
            {
                breaker.Tripped = true;
                breaker.TrippedAt = DateTimeOffset.UtcNow;
            }
        }
        else
        {
            breaker.ConsecutiveLossCount = 0;
        }

        breaker.LastEvaluatedPositionId = position.Id;
        breaker.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
