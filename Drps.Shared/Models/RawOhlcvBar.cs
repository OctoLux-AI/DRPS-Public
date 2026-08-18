using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// Immutable, append-only raw OHLCV observation as reported by a single source.
/// No UPDATE over a previously ingested row — corrections arrive as a new row.
/// </summary>
public class RawOhlcvBar
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public SourceType Source { get; set; }

    [MaxLength(16)]
    public required string Symbol { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public required string Resolution { get; set; }

    public decimal Open { get; set; }

    public decimal High { get; set; }

    public decimal Low { get; set; }

    public decimal Close { get; set; }

    public long Volume { get; set; }

    public required string AdjustmentType { get; set; }

    public DateTimeOffset IngestedAt { get; set; }

    public Guid RequestId { get; set; }
}
