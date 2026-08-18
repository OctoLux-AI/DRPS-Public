using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// Immutable, append-only scalar field observation (market cap, dividend yield, etc.)
/// as reported by a single source. No UPDATE over a previously ingested row —
/// corrections arrive as a new row.
/// </summary>
public class RawFieldObservation
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public SourceType Source { get; set; }

    [MaxLength(16)]
    public required string Symbol { get; set; }

    public required string FieldName { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public decimal? Value { get; set; }

    public string? RawValueText { get; set; }

    public DateTimeOffset IngestedAt { get; set; }

    public Guid RequestId { get; set; }
}
