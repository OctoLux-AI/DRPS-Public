using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

public enum TrustState
{
    Candidate,
    Trusted,
    Dead
}

/// <summary>
/// Per-source, per-field trust state: promotion progress toward Trusted and
/// dead-source detection via consecutive exhausted-retry failures.
/// </summary>
public class SourceStatus
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public SourceType Source { get; set; }

    public required string FieldOrBarType { get; set; }

    public TrustState TrustState { get; set; }

    public int ConsecutiveFailures { get; set; }

    public int MatchedObservationCount { get; set; }

    public DateTimeOffset? LastSuccessAt { get; set; }

    public DateTimeOffset? LastFailureAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
