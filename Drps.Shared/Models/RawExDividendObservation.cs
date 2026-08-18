using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// Immutable, append-only ex-dividend observation as reported by a single source. Slow-
/// moving reference data - matches the 7-day staleness TTL bucket CLAUDE.md defines for
/// shares-outstanding/market-cap style fields, not the 15-minute intraday-price TTL.
/// Single-source only for now (no second ex-dividend source is wired in yet), so
/// <see cref="Verified"/> is always false per the existing n=1-ingested-but-unverified
/// tiering rule - this is informational reference data that doesn't gate a decision, so
/// single-source is acceptable per CLAUDE.md's own tiering language. Flat Value +
/// Provenance shape directly on this table (value/source/fetched_at/sample_count/
/// variance_pct/verified) rather than the raw/derived-verification table split RawOhlcvBar
/// uses, since there is no second source to reconcile against yet - a parallel
/// verification table that could only ever read Verified=false would be complexity with no
/// payoff. No UPDATE over a previously ingested row - corrections arrive as a new row.
/// </summary>
public class RawExDividendObservation
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public SourceType Source { get; set; }

    [MaxLength(16)]
    public required string Symbol { get; set; }

    // The ex-dividend date itself - the datapoint this row exists to report.
    public DateOnly ExDividendDate { get; set; }

    // Dividend amount per share, as declared - never a retroactively adjusted figure,
    // matching the same raw-vs-adjusted discipline CLAUDE.md requires for OHLCV bars.
    public decimal Value { get; set; }

    // N for this observation. Always 1 today (single-source) - carried as a real column
    // rather than a hardcoded assumption so a future second source can raise it without a
    // schema change.
    public int SampleCount { get; set; } = 1;

    // Divergence between redundant sources, where applicable. Always null today - nothing
    // to compare against with only one source.
    public decimal? VariancePct { get; set; }

    // Did "ours" match "theirs" within tolerance. Always false today - n=1 can never meet
    // the existing n=2 cross-verification floor.
    public bool Verified { get; set; }

    public DateTimeOffset IngestedAt { get; set; }

    public Guid RequestId { get; set; }
}
