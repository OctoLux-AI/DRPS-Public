using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// One row per (SnapshotDate, Ticker) - the full NASDAQ + NYSE/AMEX universe as of that date,
/// ported from CapitalFill's TickerUniverseService but replacing its upsert-in-place
/// persistence (BulkUpsertTickerUniverseAsync) with an append-only dated snapshot, per
/// CLAUDE.md's "Universe Source and Persistence" decision. An upsert-in-place table can only
/// ever answer "who's in the universe now" - it silently erases the ability to know who was
/// tradable/listed on a past date, which Drps.Monolith's future point-in-time replay needs.
/// A given date's snapshot, once written, is never updated or deleted - a correction arrives
/// as a new snapshot date, same Immutability cornerstone as every other raw table in this
/// codebase.
/// </summary>
public class UniverseSnapshot
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public DateOnly SnapshotDate { get; set; }

    [MaxLength(16)]
    public required string Ticker { get; set; }

    // "NASDAQ" or "NYSE" - ported as-is from CapitalFill's TickerUniverseService, which tags
    // every otherlisted.txt row "NYSE" regardless of whether that row's own Exchange column
    // says NYSE, AMEX, or ARCA. Not revisited here - this task's scope is reusing the source
    // exactly, not correcting or refining CapitalFill's tagging.
    [MaxLength(16)]
    public required string Exchange { get; set; }
}
