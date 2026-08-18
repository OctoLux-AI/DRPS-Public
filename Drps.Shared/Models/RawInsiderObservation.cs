using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// Immutable, append-only record of one individual Form 4 purchase transaction (Insider
/// Form 4 Adjuster Multiplier Decision, CLAUDE.md 2026-07-19) - purchase-side only
/// (transactionCode == "P"), no insider-selling penalty logic, matching CapitalFill's own
/// scope rather than expanding it. Deliberately stores one row per transaction, not a
/// pre-aggregated dollar total - aggregation (60-day trailing dollar-weighted sum, per the
/// decision block) happens later in a lookup service, the same "raw rows in, aggregation at
/// read time" shape RawExDividendObservation/RawSectorObservation already use. Slow-moving
/// reference data - matches the 7-day-class staleness TTL bucket CLAUDE.md defines for
/// sector/earnings data, not the 15-minute intraday-price TTL. Single-source only for now
/// (SEC EDGAR, no second Form 4 source is wired in), n=1/informational tier - same pattern
/// as RawSectorObservation/RawEarningsObservation. No UPDATE over a previously ingested
/// row - corrections arrive as a new row.
/// </summary>
public class RawInsiderObservation
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(16)]
    public required string Ticker { get; set; }

    public SourceType Source { get; set; }

    // The actual Form 4 transaction date (when the purchase happened), not the later filing/
    // acceptance date - the 60-day trailing window in the decision block is anchored to this.
    public DateOnly TransactionDate { get; set; }

    // Shares x price for this single transaction. Never a running/aggregated total - see
    // this entity's own doc comment for why aggregation is deferred to read time.
    public decimal DollarValue { get; set; }

    // Manual-reference only, never used in scoring/aggregation math (per the decision
    // block's own scope) - nullable, since a filing can parse successfully without a
    // cleanly extractable reporting-owner name.
    [MaxLength(256)]
    public string? InsiderName { get; set; }

    public DateTime FetchedAt { get; set; }

    // True only when the filing was successfully parsed with a usable transaction code ==
    // "P" and a computable dollar value; false on fetch/parse failure or any other
    // transaction code. Fail-closed default enforced at the database level (see
    // RawInsiderObservationConfiguration), same precedent as RawSectorObservation/
    // RawEarningsObservation.
    public bool Verified { get; set; }
}
