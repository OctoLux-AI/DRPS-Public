using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

public enum OhlcvField
{
    Open,
    High,
    Low,
    Close
}

/// <summary>
/// Append-only weekly finding from the Alpaca-vs-Tiingo data-quality audit (CLAUDE.md, "Weekly
/// Data-Quality Audit: Alpaca vs. Tiingo Variance", 2026-07-22). Distinct purpose from
/// Discrepancy: BarReconciliationService's per-bar reconciliation corrects individual bars at
/// ingestion time, but cannot ask whether the pattern that justified its own narrow OHL-agreed/
/// Close-resolved-to-Tiingo exception is still true, or has drifted - this table is that
/// pattern-level check, run weekly rather than nightly, over the raw variance itself with no
/// pass/fail judgment attached (CLAUDE.md is explicit: no threshold is set yet, pending several
/// weeks of real observed data).
///
/// One row per (Ticker, BarDate, Field) - deliberately NOT deduplicated against a prior run for
/// the same week; a manual re-run of the same week legitimately appends duplicate rows, same
/// "logged raw, no deduplication" precedent as Discrepancy. Never updated or deleted once
/// written, per this codebase's Immutability & Extensibility cornerstone.
/// </summary>
public class WeeklyVarianceAuditEntry
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(16)]
    public required string Ticker { get; set; }

    // The specific trading date this comparison covers - distinct from WeekEndingDate (the
    // batch/run identifier below), since a single week can contain up to 5 trading days, each
    // producing its own row per field.
    public DateOnly BarDate { get; set; }

    public OhlcvField Field { get; set; }

    public decimal AlpacaValue { get; set; }

    public decimal TiingoValue { get; set; }

    // |AlpacaValue - TiingoValue| - stored pre-computed, same convenience precedent as
    // Discrepancy.PercentDiff, even though both are trivially derivable from the two raw values
    // above.
    public decimal AbsoluteVariance { get; set; }

    // AbsoluteVariance / AlpacaValue - kept alongside AbsoluteVariance (not in place of it)
    // since the eventual threshold, once real data justifies setting one, may end up hybrid
    // (percentage OR absolute-dollar floor) the same way BarReconciliationService's own
    // tolerance already is - both shapes of "raw variance distribution" are captured now,
    // rather than guessing which one the eventual threshold will need.
    public decimal PercentVariance { get; set; }

    // The audit run's own batch identifier - the Friday (or whichever day the job is anchored
    // to) the 7-day comparison window ends on. Every row from the same scheduled run shares
    // this value.
    public DateOnly WeekEndingDate { get; set; }

    public DateTimeOffset DetectedAt { get; set; }
}
