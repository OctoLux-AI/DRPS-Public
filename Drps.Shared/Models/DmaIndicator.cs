using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// CLAUDE.md's "Calculator Ticker Source: Watchlist + Discovered-Aligned Union, Not a Swap"
/// (2026-07-30) - records why a given symbol was in this run's compute scope. Watchlist means
/// the symbol came only from the static, permanent WatchlistOptions.Symbols array;
/// DiscoveredAligned means it came only from RollingDmaStates reporting full 5&gt;15&gt;30&gt;60
/// alignment as of this run; Both means it appeared in both sources for this run. Same
/// provenance-flag intent as Position.OpenOrigin/CloseOrigin - a future Drps.Monolith pass can
/// ask whether discovered candidates perform differently than the hand-picked Watchlist basket.
/// String-backed at the persistence layer (DmaIndicatorConfiguration.HasConversion&lt;string&gt;()),
/// same enum-pinning precedent as PositionActionOrigin/PositionExitReason/GateScore.Bucket.
/// </summary>
public enum TickerSourceOrigin
{
    Watchlist,
    DiscoveredAligned,
    Both
}

/// <summary>
/// Computed simple-moving-average value for one symbol/date/window, tagged with a
/// calculation-version identifier per the Immutability & Extensibility rule. Append-only:
/// a formula change adds a new versioned row set, it never rewrites an existing one.
/// </summary>
public class DmaIndicator
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(16)]
    public required string Symbol { get; set; }

    public DateOnly BarDate { get; set; }

    // 5, 15, 30, or 60 - also doubles as the bar count that fed this average, since a DMA
    // value is only ever emitted once exactly Window bars are available.
    public int Window { get; set; }

    public decimal Value { get; set; }

    // Informational data-quality tag, same category as Verified - never gates or alters
    // computation. True when a RawExDividendObservation's ex-dividend date falls within this
    // window's date span, per CLAUDE.md's Ex-Dividend Handling decision (2026-07-14): raw
    // prices stay unadjusted everywhere, so a window spanning an ex-div date shows a
    // mechanical price drop that isn't a real signal - this flag lets a future consumer
    // (Gate) know that, without this Engine deciding anything about it.
    public bool HasExDividendEvent { get; set; }

    // True when any date within this row's lookback window used
    // BarVerification.PrimarySourceValue (Tiingo's Close) in place of Alpaca's raw Close - see
    // DmaComputationService.GetTiingoCorrectedClosesAsync's doc comment for the full scope
    // boundary (CLAUDE.md, "Reconciliation: Narrow Tiingo-Close Exception for the OHL-Agrees/
    // C-Disagrees Signature", 2026-07-17). Informational provenance tag, same category as
    // HasExDividendEvent - never gates or alters anything downstream on its own.
    public bool HasTiingoCorrectedClose { get; set; }

    // CLAUDE.md's "Calculator Ticker Source: Watchlist + Discovered-Aligned Union, Not a
    // Swap" (2026-07-30) - which compute-scope source(s) included this symbol for the run
    // that produced this row. Required, no default (same reasoning as Position.OpenOrigin's
    // own doc comment: an optional/default-valued provenance field is exactly what turned
    // EntryAtr/TpTargetPrice/HighWaterMark into an accidental, non-authoritative correlate
    // instead of a real signal) - Worker.cs always supplies a real, known origin for every
    // symbol it processes.
    public TickerSourceOrigin TickerSourceOrigin { get; set; }

    public int CalculationVersion { get; set; }

    public DateTimeOffset ComputedAt { get; set; }
}
