using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

public enum PositionExitReason
{
    AtrStop,
    CompositeDegradation,
    PlateauDeactivation,
    SectorDisplacement,

    // CLAUDE.md's Execution Layer: Fifth Design Decision (Ledger/Alpaca Reconciliation) - a
    // "phantom" position (Ledger believes open, Alpaca's real account shows it gone) that
    // ReconciliationService confirmed via a genuinely filled closing order in Alpaca's own
    // order history, not any of the four existing reasons above - none of which describe
    // "reconciliation discovered this was closed via an order we didn't originally track."
    ReconciliationHealed,

    // CLAUDE.md's "Adjuster: Concurrent-Position-Cap Displacement, 10% Relative Composite-
    // Score Margin" (2026-08-01) - distinct from SectorDisplacement even though the
    // underlying mechanism (displace the weakest holding by composite score) is the same
    // shape, since the two represent genuinely different triggering constraints (account-wide
    // position count vs. sector capital percentage) and Drps.Monolith's later grading needs
    // to be able to tell them apart.
    PositionCountDisplacement
}

/// <summary>
/// CLAUDE.md's Execution Layer: Tenth Design Decision - records which write path performed a
/// given Position action (open or close), tracked independently per action rather than once per
/// row, since a position can be opened by one path and closed by the other (e.g. an automated
/// open followed by a manual emergency close). String-backed at the persistence layer
/// (PositionConfiguration.HasConversion&lt;string&gt;()), same enum-pinning precedent as
/// PositionExitReason/GateScore.Bucket, so a stored value never silently renumbers if members
/// are ever reordered.
///
/// Unknown is reserved exclusively for backfilling Position rows that predate this column - it
/// is never a valid value for a newly-written action. Every current write path
/// (ManualPositionEntryService hardcodes Manual, FillConfirmationService and
/// ReconciliationService hardcode Automated) always supplies a real, known origin; there is no
/// code path that leaves origin unspecified for a new action.
/// </summary>
public enum PositionActionOrigin
{
    Manual,
    Automated,
    Unknown
}

/// <summary>
/// Drps.Ledger's core record - what actually happened to a real, manually-entered trade,
/// independent of Gate's own point-in-time scoring decision (Gate/Ledger/Monolith Role
/// Clarification, Third Decision). Append-only per the Immutability cornerstone: a position's
/// lifecycle fields are updated in place as narrow, named exceptions while it remains open
/// (Ledger: Fourth Design Decision's Gate/Adjuster write carve-out), but the row itself is
/// never deleted, and a closed position is never reopened or edited - a correction arrives as
/// a new row, same as every other table in this codebase.
///
/// LowGradeDate/PlateauDate/ReactivatedDate/DeactivatedDate/CoolDownStartDate are Gate's own
/// recommendation stamps, written independently of ExitDate - since execution is manual
/// (Ledger: First Design Decision), these will often NOT land on the same timestamp as
/// ExitDate. LowGradeDate marks when Gate flagged the position; ExitDate marks when the human
/// actually recorded the close. Do not treat these as redundant or assume they coincide.
/// </summary>
public class Position
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(16)]
    public required string Ticker { get; set; }

    // FK to GateScore.Id - the scored candidate that produced this position.
    public long GateScoreId { get; set; }

    // FK to AdjusterAllocation.Id - the sizing decision that produced this position. Also the
    // read-through path for ShareCapDeficient (see LedgerPositionStateProvider.
    // GetShareCapDeficient) - consolidated per CLAUDE.md's 2026-07-18 decision block, point 4:
    // AdjusterAllocation.ShareCapDeficient is the single real, wired value, set at sizing time;
    // Position deliberately does NOT carry its own independently-written copy of this flag.
    public long AdjusterAllocationId { get; set; }

    public DateTime EntryDate { get; set; }

    public decimal EntryPrice { get; set; }

    // decimal(18,9) - matches Data Type Discipline's fractional-share quantity precision.
    public decimal EntryQuantity { get; set; }

    // CLAUDE.md's Execution Layer: Tenth Design Decision - which write path opened this row.
    // Required by LedgerPositionWriter.OpenPositionAsync's own required parameter (no default) -
    // never left to a nullable/optional shape, since that exact shape is what turned
    // EntryAtr/TpTargetPrice/HighWaterMark into an accidental, non-authoritative correlate
    // instead of a real signal. Unknown is reserved for rows backfilled from before this column
    // existed - never a value a new write supplies.
    public PositionActionOrigin OpenOrigin { get; set; }

    // Null until closed.
    public DateTime? ExitDate { get; set; }

    public decimal? ExitPrice { get; set; }

    public decimal? ExitQuantity { get; set; }

    // Null until closed - lets Drps.Monolith grade each exit type separately rather than
    // lumping all exits together.
    public PositionExitReason? ExitReason { get; set; }

    // CLAUDE.md's Execution Layer: Tenth Design Decision - which write path closed this row.
    // Independent of OpenOrigin above - a position opened by one path can be closed by the
    // other, so origin is tracked per-action, not once per row. Null until closed, same
    // convention as ExitDate/ExitPrice/ExitQuantity/ExitReason. Required by
    // LedgerPositionWriter.ClosePositionAsync's own required parameter (no default), same
    // reasoning as OpenOrigin. UpdateCircuitBreakerAsync reads this to filter the
    // consecutive-loss circuit breaker to automated closes only - a manual close (win or loss)
    // must not increment, reset, or otherwise touch it.
    public PositionActionOrigin? CloseOrigin { get; set; }

    // Gate's recommendation stamp - see class-level comment. Not the same moment as ExitDate.
    public DateTime? LowGradeDate { get; set; }

    // Gate's recommendation stamp - see class-level comment.
    public DateTime? PlateauDate { get; set; }

    // Gate's recommendation stamp - see class-level comment.
    public DateTime? ReactivatedDate { get; set; }

    // Gate's recommendation stamp - see class-level comment.
    public DateTime? DeactivatedDate { get; set; }

    // NoBuy-list start (Gate: Seventh Design Decision, revised NoBuy duration) - Gate's
    // recommendation stamp, see class-level comment.
    public DateTime? CoolDownStartDate { get; set; }

    // Adjuster's recommendation stamp (CLAUDE.md's "Adjuster: Concurrent-Position-Cap
    // Displacement, 10% Relative Composite-Score Margin," 2026-08-01) - set when this
    // position is the account-wide weakest-composite-score holding displaced to fund a new,
    // sufficiently-stronger BUY candidate while the account sits at MaxConcurrentPositions.
    // Same shape as LowGradeDate: a recommendation only, written by
    // LedgerLifecycleStampService, never accompanied by ExitDate/ExitReason at the same
    // moment (a human, or a future automated close path, performs the actual close later,
    // same as every other exit reason). Deliberately does NOT set CoolDownStartDate - that
    // field, and the NoBuy list it drives, is specific to composite-degradation whipsaw
    // protection, a different exit category entirely; a position-count displacement carries
    // no such whipsaw risk, same reasoning already applied to SectorDisplacement.
    public DateTime? DisplacementDate { get; set; }

    // Plateau reassessment baseline (2026-07-18 decision block, point 3). Set together,
    // always in pairs - ManualPositionEntryService.OpenPositionAsync seeds both from
    // EntryPrice/EntryDate at position-open time, and every plateau reassessment resets both
    // together (on trigger, and again on a Reactivated outcome). Null is reserved for a
    // position row created before this schema existed - a real gap, not "never plateau-
    // checked yet" (which is instead represented by a non-null baseline whose date simply
    // hasn't triggered a reassessment). GateScanService's plateau loop skips (logs, does not
    // stamp) any open position where this is null, rather than guessing a synthetic baseline.
    public decimal? PlateauBaselinePrice { get; set; }

    public DateTime? PlateauBaselineDate { get; set; }

    // EF Core concurrency token (SQL Server ROWVERSION, see PositionConfiguration.IsRowVersion())
    // - guards against two writers racing to close (or otherwise update) the same row.
    // Store-generated on every insert/update; never set by application code. Null only for an
    // entity that hasn't been round-tripped through the database yet.
    public byte[]? RowVersion { get; set; }

    // ATR ratchet stop fields (CLAUDE.md's Execution Layer: Third/Fourth Design Decisions).
    // Both seeded once at fill-confirmation time from the GateScore row that triggered the
    // fire - NOT re-fetched fresh, so the ratchet's baseline stays tied to the ATR value Gate
    // actually saw when it made the recommendation. Same "nullable means legacy/pre-schema
    // row" convention as PlateauBaselinePrice/PlateauBaselineDate: a position opened through
    // FillConfirmationService always has both; only a position predating this schema (or one
    // opened via the manual CLI, where no automated fill/GateScore.AtrValue link applies) has
    // either as null.
    public decimal? EntryAtr { get; set; }

    // EntryPrice + N x EntryAtr, N=2 per the locked addendum to the Third Design Decision.
    // Fixed at entry using the ATR snapshot above, never recalculated against a later, live
    // ATR reading - if the target moved with a live ATR feed, "progress toward it" would be a
    // moving goalpost and the ratchet concept stops meaning anything.
    public decimal? TpTargetPrice { get; set; }

    // Highest live price observed since entry (AtrRatchetMonitorWorker) - named in CLAUDE.md's
    // Execution Layer: Third Design Decision's stop formula (HighWaterMark - multiplier x
    // currentAtr) but never actually added as a persisted column until this task closed that
    // gap. Same "nullable means legacy/pre-schema row" convention as EntryAtr/TpTargetPrice -
    // seeded to EntryPrice at open time (FillConfirmationService), only ever moves up from
    // there. A position with EntryAtr/TpTargetPrice set but this still null (a narrow gap
    // between when those two columns were added and when this one was) is self-healing:
    // AtrRatchetMonitorWorker falls back to EntryPrice as the starting high-water mark the
    // first time it evaluates such a row, then persists it going forward.
    public decimal? HighWaterMark { get; set; }

    // Raw, uncapped ratchet progress (see AtrRatchetMonitorWorker's progress calculation) at
    // the moment an ATR-stop exit fired - distinguishes a fast stop-out (progress near 0) from
    // a near-target ratchet exit (progress near 1) for Drps.Monolith's later grading, without
    // forking PositionExitReason itself (CLAUDE.md's Execution Layer: Third Design Decision,
    // "Grading distinction, without forking behavior"). Null for every exit reason other than
    // AtrStop, and for any position that predates this column.
    public decimal? TpProgressAtExit { get; set; }
}
