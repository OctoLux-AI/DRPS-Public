using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

public enum GateBucket
{
    Buy,
    Watch,
    Exit,
    Neutral,

    // Purely observational (CLAUDE.md's "2026-08-04 — Sleeper Bucket: Observational Tracking
    // for Pre-DMA-Alignment Momentum") - a ticker that failed DMA alignment (Tier 1, Stage 1)
    // but shows confirmed-positive RsiSlope/RsiConcavity momentum, verified. Explicitly NOT a
    // tradeable bucket: AdjusterScanService/OpenCandidateQuery both allowlist only
    // GateBucket.Buy, so this value is structurally excluded from ever being sized or fired -
    // by construction, not by convention alone. String-backed (HasConversion<string>() on
    // GateScoreConfiguration), so adding this member never risks renumbering Buy/Watch/Exit/
    // Neutral's already-stored values.
    Sleeper
}

/// <summary>
/// Gate's own append-only, calculation-versioned output row - the decision alone, per
/// CLAUDE.md's Gate/Ledger/Monolith role clarification. Deliberately does NOT carry the
/// outcome of that decision (that lives on Drps.Ledger) - bundling a decision with its own
/// result would make it impossible for Drps.Monolith to grade Gate honestly later. Position-
/// lifecycle fields (LowGradeDate, PlateauDate, ReactivatedDate, DeactivatedDate,
/// ShareCapDeficient) are likewise excluded - they describe what happened to a held
/// position over time, not a point-in-time scoring decision, and live on Drps.Ledger.
/// </summary>
public class GateScore
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    // As-of timestamp for this scan, sourced from Worker's injectable clock seam - never
    // DateTime.Now directly. Lets Drps.Monolith later replay this exact scan against a
    // historical date.
    public DateTime ScanDate { get; set; }

    [MaxLength(16)]
    public required string Ticker { get; set; }

    // Sector-cap enforcement (Adjuster). Data dependency not yet built in Ingestion.
    // Nullable, deliberately - null means "cannot verify sector," a real, honest signal
    // Adjuster's future sector-cap logic must check for and exclude from cap enforcement
    // (while still leaving the candidate individually fundable), not a shared fake category
    // like "Unknown" or empty string.
    public string? Sector { get; set; }

    // Binary Tier 1 gate - perfect 5>15>30>60 alignment or rejected, no partial credit.
    public bool IsDmaAligned { get; set; }

    // "Fully verified" (all four windows AND'd) - carried as data only, per Gate: Fifth
    // Design Decision. Prior to the blast-radius-scoping decision (CLAUDE.md 2026-08-04) this
    // was ALWAYS true on any written row, since it was the Tier 1 gate itself - any candidate
    // failing it never reached persistence. As of that decision it can genuinely be false on
    // a written row: Tier 1 now gates on IsDma5VerifiedAsync_Result alone, so a candidate can
    // score with DMA-15/30/60 unverified. See the four per-window fields below for the
    // breakdown this aggregate summarizes.
    public bool IsDmaVerifiedAsync_Result { get; set; }

    // Per-window verification breakdown (blast-radius scoping, CLAUDE.md 2026-08-04) - four
    // separate bools rather than one combined flag, same "two separate bools, not one
    // combined flag" convention this entity already uses elsewhere (HasTiingoCorrectedData/
    // HasUnverifiedPartialCreditData, IsEarningsBlackoutActive/EarningsDataUnverified). A bad
    // bar 45 trading days old fails IsDma60VerifiedAsync_Result while IsDma5/15/30 stay true,
    // since their own rolling lookbacks never reach that far back. IsDma5VerifiedAsync_Result
    // is, by construction, always true on any row that exists (it is now literally what Tier
    // 1 gates on) - still recorded explicitly, for the same completeness/parallel-structure
    // reasoning as the other three, not because it can vary.
    public bool IsDma5VerifiedAsync_Result { get; set; }

    public bool IsDma15VerifiedAsync_Result { get; set; }

    public bool IsDma30VerifiedAsync_Result { get; set; }

    public bool IsDma60VerifiedAsync_Result { get; set; }

    public decimal RsiValue { get; set; }

    // 0.0-1.0 quality score, peaked curve (50/60/70 anchors) - Tier 1, non-negotiable.
    public decimal RsiQuality { get; set; }

    public decimal RvolValue { get; set; }

    // 0.0-1.0 quality score, provisionally monotonic - Tier 2, half-weight when unverified.
    public decimal RvolQuality { get; set; }

    // Used for stop-sizing, not composite scoring - excluded from ranking math per Gate:
    // Sixth Design Decision.
    public decimal AtrValue { get; set; }

    // Last clean pre-ex-div ATR reading, when applicable - Gate: Third Design Decision's
    // clean-ATR-reference fix, guards against comparing a dividend against a True Range the
    // dividend itself already distorted.
    public decimal? AtrCleanPreEventValue { get; set; }

    // RSI (70%) + RVOL (30%) only - DMA/ATR excluded per Gate: Sixth Design Decision.
    public decimal CompositeScore { get; set; }

    public GateBucket Bucket { get; set; }

    // Rejection persistence (CLAUDE.md's "Gate: Rejection Reasons Now Persisted,"
    // 2026-08-06) - null means this row represents a real, non-rejected candidate; a
    // non-null value names the GateRejectionReason (Drps.Gate.Scoring) that caused the
    // rejection, e.g. "DmaNotAligned", "RsiNotVerified". Stored as a plain string, not a
    // real enum reference: GateRejectionReason lives in Drps.Gate.Scoring, and
    // Drps.Shared has zero project references by design (persistence-agnostic, lowest-
    // level shared library) - GateScanService converts the enum via .ToString() at the
    // persistence boundary; the enum itself is untouched. A rejected row always carries
    // Bucket = Neutral (reusing the existing Sleeper-row precedent, not a new Bucket
    // value) - RejectionReason is what actually distinguishes a rejection from a
    // genuine, ordinary low-composite-score Neutral candidate.
    public string? RejectionReason { get; set; }

    // Carried from HasExDividendEvent, scored identically to an unflagged candidate by
    // default per Gate: Second Design Decision - recorded so Drps.Monolith can later ask
    // whether scoring through an ex-div event was the right call.
    public bool ScoredThroughExDividendEvent { get; set; }

    // Carried from RSI/AtrIndicator, purely informational - never gates or discounts
    // anything, per Gate: Second Design Decision.
    public bool VerificationScopeLimited { get; set; }

    // Data-quality provenance, added after this session's audit found that whether a score
    // touched corrected or unverified data was invisible at the GateScore level - nowhere
    // near where a human would actually look before manually acting on a recommendation
    // (there is no automated exit/consumer of GateScore today, confirmed by that same audit).
    // Two separate bools rather than one combined flag: both can be true simultaneously (e.g.
    // DMA's window used a Tiingo-corrected bar AND RVOL passed on unverified partial credit),
    // and collapsing that into a single boolean would silently lose which case(s) applied.
    //
    // True if any DMA/RSI/RVOL/ATR indicator row that fed this score had
    // HasTiingoCorrectedClose == true (i.e. BarReconciliationService's narrow OHL-agreed/
    // Close-resolved-to-Tiingo exception touched a bar in this score's contributing lookback
    // windows - see the provenance fields added to DmaIndicator/RsiIndicator/RvolIndicator/
    // AtrIndicator in the prior task).
    public bool HasTiingoCorrectedData { get; set; }

    // True if this candidate reached scoring via the Tier 2 partial-credit path despite an
    // unverified bar - RVOL unverified (half weight, never rejects) or ATR unverified with a
    // backstop stop control (bucket capped at WATCH, never rejects). DMA/RSI can never
    // contribute to this flag: both are Tier 1, non-negotiable, hard-reject-on-unverified -
    // any candidate that reaches GateScore at all already had both fully verified.
    public bool HasUnverifiedPartialCreditData { get; set; }

    // Staleness detection (this session's scheduling-resilience audit) - the oldest BarDate
    // among the four contributing indicators (DMA/RSI/RVOL/ATR) that fed this score. Always
    // populated: a candidate only reaches GateScore once all four have resolved to a real
    // BarDate. Lets a human directly see how old the underlying data actually was, without
    // cross-referencing DmaIndicator/RsiIndicator/RvolIndicator/AtrIndicator's own BarDate/
    // ComputedAt columns by hand - the exact manual-cross-reference problem this field exists
    // to close. See GateScanService's own staleness-check comment for the full mechanism.
    public DateOnly DataAsOfDate { get; set; }

    // True when DataAsOfDate is more than one trading weekday behind this scan's ScanDate -
    // GateScanService's own comment explains why "more than one weekday" (not "not literally
    // today") is the threshold: the healthy, expected state at every scan is data from the
    // most recently completed trading day, computed hours earlier that same morning, which is
    // exactly a one-weekday gap. A gap beyond that means either a genuine pipeline outage
    // (Ingestion/Calculator didn't run) or a real multi-day holiday cluster - this flag does
    // not distinguish those two further; it flags for human review rather than guessing, per
    // this session's explicit "warn/flag rather than guess" scope decision. Never blocks
    // scoring - a stale candidate is still scored and bucketed normally, just visibly flagged.
    public bool IsStaleData { get; set; }

    public int CalculationVersion { get; set; }

    // FK to GateParameters.Id - which tunable-threshold version was active for this scan.
    public long GateParameterVersion { get; set; }

    // Set on composite-degradation exit (<0.70) - ticker excluded from re-entry until this
    // passes, per Gate: Seventh Design Decision (revised NoBuy duration).
    public DateTime? NoBuyListExpiration { get; set; }

    // Earnings Blackout Gate Decision (CLAUDE.md 2026-07-19) - a hard gate excluding a
    // candidate from BUY, never folded into CompositeScore. These two bools are deliberately
    // separate (not one combined flag), same reasoning as HasTiingoCorrectedData/
    // HasUnverifiedPartialCreditData above: a human reviewing results must be able to tell
    // "genuinely blacked out" (a real, verified, imminent earnings date - EarningsLookupService's
    // state (b)) apart from "we don't know" (missing/stale/failed earnings data - state (c)).
    // At most one of the two is ever true; both false means a fresh, verified check found no
    // imminent earnings event (state (a)).
    public bool IsEarningsBlackoutActive { get; set; }

    public bool EarningsDataUnverified { get; set; }
}
