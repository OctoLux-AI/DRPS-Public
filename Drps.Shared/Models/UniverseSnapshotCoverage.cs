using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// Which of UniverseIngestionRunner's two independent source fetches actually succeeded for a
/// given SnapshotDate - Nasdaq (nasdaqlisted.txt) and NyseAmex (otherlisted.txt, which
/// despite its "NYSE" tagging in <see cref="UniverseSnapshot.Exchange"/> also covers AMEX/ARCA).
/// [Flags] so a single value can represent both-succeeded (<see cref="All"/>), either single
/// source, or neither.
/// </summary>
[Flags]
public enum UniverseSourceCoverage
{
    None = 0,
    Nasdaq = 1,
    NyseAmex = 2,
    All = Nasdaq | NyseAmex
}

/// <summary>
/// Explicit, one-row-per-SnapshotDate visibility record for whether that day's
/// <see cref="UniverseSnapshot"/> reflects the full universe or a partial one - CLAUDE.md's
/// fail-closed-everywhere principle applied to a gap this codebase didn't previously cover:
/// UniverseIngestionRunner's independent per-source try/catch means a single source's failure
/// (say, NASDAQ succeeded but NYSE/AMEX's fetch failed that night) still persists a partial
/// snapshot for the day, with nothing on the ticker rows themselves distinguishing that day
/// from a normal, fully-covered one. This record makes that distinction explicit and queryable
/// rather than requiring a downstream consumer to somehow infer it (e.g. by comparing today's
/// ticker count against a historical baseline).
///
/// Deliberately a separate table, not a column repeated across every one of that date's
/// (potentially ~10,000) UniverseSnapshot ticker rows - the fact being recorded is a property
/// of the day's ingestion run, not of any individual ticker, so it belongs on its own row,
/// same "one row per real-world event" discipline as WorkerRunRecord/KillSwitchCounter.
///
/// Visibility only - deliberately does NOT gate or block any downstream consumer (the bar
/// sweep, the Rolling DMA State Machine). A partial day's surviving tickers are still real,
/// still worth processing; the point is that the gap must be loud (logged), never silent.
/// Append-only, like every other raw/derived table in this codebase - once written for a date,
/// never updated in place.
/// </summary>
public class UniverseSnapshotCoverage
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public DateOnly SnapshotDate { get; set; }

    public UniverseSourceCoverage SourcesSucceeded { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
}
