using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// The tri-state outcome of one FinnhubEarningsFeeder fetch attempt (CLAUDE.md's "Earnings
/// Verification Tri-State Fix," 2026-08-07) - replaces the original <c>Verified</c> bool,
/// which collapsed "queried successfully, genuinely nothing in the lookahead window" and
/// "response couldn't be parsed / unexpected shape" into the identical false value. Those
/// are not the same outcome: the first is a safe, blackout-clear result; the second is a
/// genuinely unknown state that must stay fail-closed.
/// </summary>
public enum EarningsFetchOutcome
{
    // Fail-closed default (CLAUDE.md's fail-closed sentinel principle) - the response
    // couldn't be parsed as expected (root not an object, no earningsCalendar property, or
    // earningsCalendar not an array), or the fetch itself failed (non-2xx after retries,
    // malformed JSON body, network error). Genuinely unknown - never treated as "safe."
    Unknown = 0,

    // earningsCalendar was present and well-formed as an array, but yielded zero entries
    // with a parseable date >= the lookahead window's start after filtering - a safe,
    // positively-confirmed "nothing upcoming" result, not an unknown one.
    NoUpcomingEarningsInWindow = 1,

    // earningsCalendar yielded at least one entry with a parseable date >= start.
    // NextEarningsDate holds the earliest such date.
    UpcomingEarningsFound = 2
}

/// <summary>
/// Immutable, append-only earnings-date observation as reported by a single source. Slow-
/// moving reference data - matches the 7-day staleness TTL bucket CLAUDE.md defines for
/// shares-outstanding/market-cap style fields, not the 15-minute intraday-price TTL.
/// Single-source only for now (Finnhub, no second earnings-calendar source is wired in
/// yet) - <see cref="FetchOutcome"/> records what actually happened on this fetch attempt
/// (never a cross-source reconciliation result, since there is nothing to reconcile
/// against). Flat Value + Provenance shape directly on this table, same precedent as
/// RawSectorObservation/RawExDividendObservation. No UPDATE over a previously ingested
/// row - corrections arrive as a new row.
/// </summary>
public class RawEarningsObservation
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(16)]
    public required string Ticker { get; set; }

    public SourceType Source { get; set; }

    // The ticker's next known earnings date, as reported by Source. Non-null only when
    // FetchOutcome == UpcomingEarningsFound; null in every other case (both
    // NoUpcomingEarningsInWindow - a legitimate "nothing found" result - and Unknown - a
    // genuine failure).
    public DateOnly? NextEarningsDate { get; set; }

    public DateTime FetchedAt { get; set; }

    // Tri-state outcome of this fetch attempt (CLAUDE.md's "Earnings Verification
    // Tri-State Fix," 2026-08-07) - replaces the original Verified bool, which conflated
    // "no upcoming earnings found" with "couldn't parse the response." Fail-closed default
    // (Unknown) enforced at the database level (see RawEarningsObservationConfiguration).
    public EarningsFetchOutcome FetchOutcome { get; set; }
}
