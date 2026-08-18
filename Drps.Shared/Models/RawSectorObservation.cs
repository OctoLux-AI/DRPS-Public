using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

// Finnhub and SecEdgar use incompatible sector/industry taxonomies, so a SecEdgar row is
// never auto-compared against a Finnhub row for this entity - kept as its own dedicated
// enum (not folded into SourceType) since it has no relationship to the OHLCV/dead-source-
// tracking sources SourceType exists for.
public enum SectorSourceType
{
    Finnhub,
    SecEdgar
}

/// <summary>
/// Immutable, append-only sector/industry classification observation as reported by a
/// single source. Slow-moving reference data - matches the 7-day staleness TTL bucket
/// CLAUDE.md defines for shares-outstanding/market-cap style fields, not the 15-minute
/// intraday-price TTL. Finnhub is primary and n=1 by design (<see cref="Verified"/> is true
/// only for Finnhub rows); SecEdgar rows are informational only and always carry
/// Verified=false, since EDGAR's sicDescription taxonomy is not compatible with Finnhub's
/// finnhubIndustry classification and the two are never reconciled against each other. Flat
/// Value + Provenance shape directly on this table, same precedent as
/// RawExDividendObservation, since there is no cross-source reconciliation to support. No
/// UPDATE over a previously ingested row - corrections arrive as a new row.
/// </summary>
public class RawSectorObservation
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(16)]
    public required string Ticker { get; set; }

    public SectorSourceType Source { get; set; }

    // Human-readable classification (e.g. Finnhub's finnhubIndustry or EDGAR's
    // sicDescription). Nullable - a source can respond without a usable classification.
    [MaxLength(256)]
    public string? SectorValue { get; set; }

    // EDGAR-only 4-digit SIC code. Always null for Finnhub rows - Finnhub has no SIC
    // equivalent.
    [MaxLength(4)]
    public string? SicCode { get; set; }

    public DateTime FetchedAt { get; set; }

    // Did "ours" match "theirs" within tolerance. Always true for a Finnhub row (primary,
    // n=1 by design - single-source reference data is acceptable per CLAUDE.md's own
    // tiering language) and always false for a SecEdgar row (informational only, never
    // reconciled against Finnhub given the incompatible taxonomies).
    public bool Verified { get; set; }
}
