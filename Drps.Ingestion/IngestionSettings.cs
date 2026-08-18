namespace Drps.Ingestion;

// Bound from configuration section "Ingestion" via IOptions<IngestionSettings>.
public class IngestionSettings
{
    public int LookbackDays { get; set; }

    // IntervalMinutes removed (scheduling audit, 2026-07-19) - Worker no longer runs on a flat
    // interval; it runs once daily at a fixed time via NextRunTimeCalculator. See Worker.cs's
    // own doc comment for why.

    // Optional explicit override for a one-off historical run (e.g. verifying a source's
    // behavior around a known past stock split). When both are set, they take precedence
    // over LookbackDays; if either is null, the LookbackDays-relative-to-today computation
    // is used unchanged.
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
}
