using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// Derived/computed reconciliation state across sources for a given OHLCV bar.
/// </summary>
public class BarVerification
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(16)]
    public required string Symbol { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public required string Resolution { get; set; }

    public int SourceCount { get; set; }

    public int MatchedSourceCount { get; set; }

    public bool Verified { get; set; }

    public decimal ToleranceApplied { get; set; }

    public decimal? PrimarySourceValue { get; set; }

    public int ComputationVersion { get; set; }

    public DateTimeOffset EvaluatedAt { get; set; }
}
