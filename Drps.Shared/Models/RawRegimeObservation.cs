using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

// Cboe is the sole authority that calculates VIX/VXN/VIX3M; every other source (FRED
// included) redistributes Cboe's own already-published number rather than independently
// observing it - see CLAUDE.md's "Regime Data Sourcing" decision (2026-07-26) for why this
// makes authority-plus-redistribution-channel the right verification model here, not the
// two-independent-feeds-disagreeing model OHLCV reconciliation uses. Kept as its own enum,
// not folded into SourceType or SectorSourceType - a third, unrelated domain.
public enum RegimeSourceType
{
    CboeDirect,
    Fred
}

/// <summary>
/// Immutable, append-only daily observation of a volatility-index level (Ticker: "VIX",
/// "VXN", or "VIX3M") as reported by a single source. No cross-source reconciliation is
/// performed here - FRED and Cboe direct rows for the same Ticker/ObservationDate are simply
/// both persisted, distinguished by <see cref="Source"/> (CLAUDE.md's "Regime Data Sourcing"
/// decision, 2026-07-26 - that reconciliation is explicitly out of scope until a later task).
/// Cboe direct is the sole authoritative source and always carries full OHLC; FRED is a
/// close-only redistribution of the same published number - Open/High/Low are left null for
/// FRED rows rather than fabricated as a same-as-close value, since DRPS never actually
/// observed those fields from FRED. No UPDATE over a previously ingested row - corrections
/// arrive as a new row.
/// </summary>
public class RawRegimeObservation
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    // VIX / VXN / VIX3M - a fixed, closed set of index tickers, kept as a plain string
    // (not an enum) for the same reason every other ticker field in this codebase is a
    // string (RawOhlcvBar.Symbol, RawSectorObservation.Ticker, Position.Ticker).
    [MaxLength(8)]
    public required string Ticker { get; set; }

    public DateOnly ObservationDate { get; set; }

    // Null for FRED rows (close-only source) - see class doc comment.
    public decimal? Open { get; set; }

    public decimal? High { get; set; }

    public decimal? Low { get; set; }

    public decimal Close { get; set; }

    public RegimeSourceType Source { get; set; }

    public DateTime FetchedAt { get; set; }
}
