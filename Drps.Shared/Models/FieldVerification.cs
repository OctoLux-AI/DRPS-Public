using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// Derived/computed reconciliation state across sources for a given scalar field
/// observation. Separate from BarVerification since tolerance/risk rules differ per field.
/// </summary>
public class FieldVerification
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(16)]
    public required string Symbol { get; set; }

    public required string FieldName { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public int SourceCount { get; set; }

    public int MatchedSourceCount { get; set; }

    public bool Verified { get; set; }

    public decimal ToleranceApplied { get; set; }

    public int ComputationVersion { get; set; }

    public DateTimeOffset EvaluatedAt { get; set; }
}
