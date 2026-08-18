using Drps.Ingestion.Persistence;
using Drps.Shared.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Drps.Ledger;

/// <summary>
/// Write path for Gate/Adjuster's lifecycle-stamp recommendations onto an existing, open
/// Position row - the mechanism specified by CLAUDE.md's "Gate/Adjuster→Ledger Lifecycle-Stamp
/// Carve-Out: Resolved (2026-07-18)" block, point 5. Distinct from ManualPositionEntryService:
/// that class is the ONLY writer of ExitDate/ExitPrice/ExitQuantity/ExitReason (human-triggered,
/// via the CLI, per Ledger: First Design Decision's manual-execution-only rule); this class
/// only ever touches the lifecycle recommendation-stamp fields, never Exit*.
///
/// Also implements the plateau reassessment path (2026-07-18 decision block, point 3):
/// PlateauDate + baseline reset, followed by either ReactivatedDate or DeactivatedDate
/// depending on the caller's own Inadequate evaluation (computed by GateScanService, not this
/// class - this class only ever persists a decision already made, same division of labor as
/// the composite-degradation path below).
/// </summary>
public class LedgerLifecycleStampService
{
    private readonly DrpsDbContext _dbContext;
    private readonly ILifecycleNotificationService _notificationService;
    private readonly ILogger<LedgerLifecycleStampService> _logger;

    public LedgerLifecycleStampService(
        DrpsDbContext dbContext,
        ILifecycleNotificationService notificationService,
        ILogger<LedgerLifecycleStampService> logger)
    {
        _dbContext = dbContext;
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <summary>
    /// Fires when GateCompositeService resolves a held position's bucket to Exit (composite
    /// score drops below ExitThreshold while held - Gate: Seventh Design Decision's
    /// composite-degradation exit). Stamps LowGradeDate and CoolDownStartDate together, at
    /// recommendation time - per the 2026-07-18 decision block's points 1/2, this does NOT wait
    /// for a human to actually close the position via the Ledger CLI. Gate never touches
    /// ExitDate/ExitPrice/ExitQuantity/ExitReason; those remain
    /// ManualPositionEntryService.ClosePositionAsync's job alone.
    /// </summary>
    public async Task StampCompositeDegradationAsync(string ticker, DateTime asOf, CancellationToken cancellationToken)
    {
        // Lookup key matches LedgerPositionStateProvider.IsCurrentlyHeld's own shape (Ticker,
        // ExitDate == null) - kept consistent rather than inventing a second lookup
        // convention (2026-07-18 decision block, point 5). Most-recent-by-EntryDate in case
        // more than one open row somehow exists for a ticker - same "latest wins" precedent
        // used throughout this codebase (e.g. LedgerPositionStateProvider.GetNoBuyExpiration).
        var position = await _dbContext.Positions
            .Where(p => p.Ticker == ticker && p.ExitDate == null)
            .OrderByDescending(p => p.EntryDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (position is null)
        {
            // Per Ledger: Fourth Design Decision's documented failure mode - Gate has no way
            // to know whether a scored BUY was ever actually filled, so "no open position"
            // is an expected, valid state, not an error. Silent no-op, logged - never an
            // exception, never a blocking failure.
            _logger.LogWarning(
                "[LEDGER-LIFECYCLE-STAMP]: {Ticker}: composite-degradation exit recommended but no open Position exists - no-op",
                ticker);
            return;
        }

        position.LowGradeDate = asOf;
        position.CoolDownStartDate = asOf;
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "[LEDGER-LIFECYCLE-STAMP]: {Ticker}: stamped LowGradeDate/CoolDownStartDate on Position {PositionId} at {AsOf}",
            ticker, position.Id, asOf);

        // Structural seam only, not live notification behavior - see
        // ILifecycleNotificationService's own doc comment. Does not change or advance the
        // existing "Pushover parked until 30 auto-completed trades" decision.
        await _notificationService.NotifyLifecycleStampAsync(
            new LifecycleStampNotification(ticker, position.Id, "CompositeDegradation", asOf),
            cancellationToken);

        // FUTURE-AUTONOMY EXTENSION POINT - not implemented, not called anywhere.
        // Ledger: First Design Decision's manual-execution-only rule means a human runs the
        // Ledger CLI's `close` command, on their own schedule, sometime after this stamp lands.
        // If/when DRPS ever builds automated execution (explicitly deferred - see CLAUDE.md's
        // Ledger: First Design Decision and the parked "denied open/close orders" item), this
        // is the point where an automatic-execution branch could call
        // ManualPositionEntryService.ClosePositionAsync (or a future execution-layer
        // equivalent) immediately, instead of waiting for that human action. Left as a comment
        // only - no method, no logic - per this task's explicit scope decision.
    }

    /// <summary>
    /// Fires when GateScanService's plateau reassessment trigger crosses for a currently-held
    /// position (ATR-cross OR 5-trading-days-elapsed, whichever comes first - 2026-07-18
    /// decision block, point 3). Always stamps PlateauDate and resets
    /// PlateauBaselinePrice/PlateauBaselineDate to <paramref name="newBaselinePrice"/>/
    /// <paramref name="asOf"/> ("today"), regardless of outcome - the reset happens "on
    /// trigger," per the decision block, not conditionally on Inadequate. <paramref
    /// name="isInadequate"/> is the caller's already-computed verdict (DMA hard-gate fail OR
    /// RSI outside band OR RVOL below floor - GateScanService's job, not this class's); this
    /// method only persists it: DeactivatedDate when true, ReactivatedDate when false. Same
    /// silent-no-op-when-no-open-position failure mode as StampCompositeDegradationAsync above.
    /// Never sets ExitDate/ExitReason - even on a Deactivated (Inadequate) outcome, a human
    /// still closes the position via the Ledger CLI (ExitReason = PlateauDeactivation is
    /// available on PositionExitReason for exactly that later, manual step).
    /// </summary>
    public async Task StampPlateauReassessmentAsync(
        string ticker, DateTime asOf, decimal newBaselinePrice, bool isInadequate, CancellationToken cancellationToken)
    {
        var position = await _dbContext.Positions
            .Where(p => p.Ticker == ticker && p.ExitDate == null)
            .OrderByDescending(p => p.EntryDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (position is null)
        {
            _logger.LogWarning(
                "[LEDGER-LIFECYCLE-STAMP]: {Ticker}: plateau reassessment triggered but no open Position exists - no-op",
                ticker);
            return;
        }

        position.PlateauDate = asOf;
        position.PlateauBaselinePrice = newBaselinePrice;
        position.PlateauBaselineDate = asOf;

        string stampReason;
        if (isInadequate)
        {
            position.DeactivatedDate = asOf;
            stampReason = "PlateauDeactivation";
        }
        else
        {
            position.ReactivatedDate = asOf;
            stampReason = "PlateauReactivation";
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "[LEDGER-LIFECYCLE-STAMP]: {Ticker}: plateau reassessment on Position {PositionId} at {AsOf} - {Outcome}, baseline reset to {NewBaselinePrice}",
            ticker, position.Id, asOf, isInadequate ? "Deactivated" : "Reactivated", newBaselinePrice);

        await _notificationService.NotifyLifecycleStampAsync(
            new LifecycleStampNotification(ticker, position.Id, stampReason, asOf),
            cancellationToken);

        // Same FUTURE-AUTONOMY EXTENSION POINT as StampCompositeDegradationAsync above - not
        // implemented here either. A Deactivated outcome still waits for a human to close via
        // the Ledger CLI (ExitReason = PlateauDeactivation), same as every other exit path.
    }

    /// <summary>
    /// Fires when Adjuster determines a held position is the account-wide weakest by
    /// composite score and a new BUY candidate exceeds it by the required relative margin
    /// while the account sits at MaxConcurrentPositions (CLAUDE.md's "Adjuster: Concurrent-
    /// Position-Cap Displacement, 10% Relative Composite-Score Margin," 2026-08-01). Stamps
    /// DisplacementDate only - deliberately does NOT set CoolDownStartDate, unlike
    /// StampCompositeDegradationAsync above; that field's NoBuy-list behavior is specific to
    /// composite-degradation whipsaw protection, a different exit category this displacement
    /// carries no risk of. Same silent-no-op-when-no-open-position failure mode as the other
    /// stamp methods on this class, and the same (Ticker, ExitDate == null) lookup convention.
    /// Adjuster never touches ExitDate/ExitPrice/ExitQuantity/ExitReason directly - a human
    /// (or, once built, a future automated close path) performs the actual close later via
    /// LedgerPositionWriter.ClosePositionAsync, same as every other recommendation stamp.
    /// </summary>
    public async Task StampPositionCountDisplacementAsync(string ticker, DateTime asOf, CancellationToken cancellationToken)
    {
        var position = await _dbContext.Positions
            .Where(p => p.Ticker == ticker && p.ExitDate == null)
            .OrderByDescending(p => p.EntryDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (position is null)
        {
            _logger.LogWarning(
                "[LEDGER-LIFECYCLE-STAMP]: {Ticker}: position-count displacement recommended but no open Position exists - no-op",
                ticker);
            return;
        }

        position.DisplacementDate = asOf;
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "[LEDGER-LIFECYCLE-STAMP]: {Ticker}: stamped DisplacementDate on Position {PositionId} at {AsOf}",
            ticker, position.Id, asOf);

        await _notificationService.NotifyLifecycleStampAsync(
            new LifecycleStampNotification(ticker, position.Id, "PositionCountDisplacement", asOf),
            cancellationToken);

        // Same FUTURE-AUTONOMY EXTENSION POINT as the other stamp methods on this class - not
        // implemented here either. A human closes the position via the Ledger CLI (ExitReason
        // = PositionCountDisplacement, once a real automated close path exists it would call
        // LedgerPositionWriter.ClosePositionAsync here instead of waiting).
    }
}
