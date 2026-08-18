using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// One row per successfully-completed scheduled Worker run - Drps.Ingestion/Drps.Calculator/
/// Drps.Gate's idempotency guard against the backward-clock re-fire bug found in this session's
/// scheduling-resilience audit (Scenario 3: a backward wall-clock correction landing between the
/// corrected time and today's already-passed scheduled run time can cause NextRunTimeCalculator
/// to compute that same calendar day's target a second time, re-firing the whole day's work).
///
/// Append-only, matching this codebase's Immutability cornerstone - "already ran today" is
/// answered by row existence for (WorkerName, RunDate), not by a mutable per-worker row updated
/// in place. WorkerName is a plain string identifying which of the three Workers recorded the
/// row (e.g. "Drps.Ingestion") - not an enum, since this is a narrow, internal bookkeeping
/// concern with no cross-referencing need elsewhere in the schema.
/// </summary>
public class WorkerRunRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(64)]
    public required string WorkerName { get; set; }

    public DateOnly RunDate { get; set; }

    public DateTime CompletedAt { get; set; }

    /// <summary>
    /// True when every required data source for this run succeeded. Defaults to true so every
    /// pre-existing caller/row (Ingestion/Calculator/Gate's single-source Worker classes,
    /// WeeklyVarianceAuditWorker, UniverseIngestionRunner, UniverseBarSweepRunner) is unaffected
    /// - none of them pass a value for this explicitly, and their runs genuinely have no
    /// per-source partial-failure concept to report. RegimeIngestionRunner is the first, and
    /// currently only, caller that sets this to false - closes CLAUDE.md's "Regime Ingestion:
    /// Partial-Success Guard Gap" (2026-07-27): a run where one or more of its five IRegimeFeeder
    /// sources (VIX/Cboe, VXN/Cboe, VXN/FRED, VIX3M/Cboe, VIX3M/FRED) failed must not look
    /// identical, via this row alone, to a run where all five succeeded.
    /// </summary>
    public bool AllSourcesSucceeded { get; set; } = true;

    /// <summary>
    /// Which source(s) failed and why, populated only when <see cref="AllSourcesSucceeded"/> is
    /// false - null on every full-success row (the overwhelming majority, including every row
    /// from every other Worker). A future consumer (e.g. a regime raw-multiplier calculation)
    /// checks <see cref="AllSourcesSucceeded"/> first and can read this for diagnostic detail
    /// without re-deriving it from log files.
    /// </summary>
    [MaxLength(2000)]
    public string? FailureDetail { get; set; }
}
