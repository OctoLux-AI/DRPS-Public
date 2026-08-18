using System.Globalization;
using System.Text;

namespace Drps.Diagnostics;

public sealed record SeriesCoverage(string Label, string SourceDescription, int Count, DateOnly FirstDate, DateOnly LastDate);

// A single locked figure from CLAUDE.md's regime-threshold decision blocks, to compare a
// freshly-computed statistic against. Comparison thresholds are stated plainly in the report
// itself, not hidden in code - see BuildComparisonRow below.
public sealed record LockedFigure(string StatisticName, double LockedValue);

public static class RegimeDistributionReportWriter
{
    // Comparison bands, stated explicitly here and restated in the report itself, per this
    // task's own instruction not to silently reconcile anything - these are reporting
    // thresholds only, they never adjust or filter a computed value.
    private const double MatchThresholdPercent = 0.5;
    private const double CloseThresholdPercent = 5.0;

    public static string BuildReport(
        DateOnly generatedDate,
        SeriesCoverage vix,
        SeriesCoverage vxn,
        SeriesCoverage vix3m,
        int vxnVixSampleCount,
        DistributionStatistics vxnVixStats,
        int vixVix3mSampleCount,
        DistributionStatistics vixVix3mStats)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Regime Distribution Analysis: VXN/VIX and VIX/VIX3M (empirical, reproducible)");
        sb.AppendLine();
        sb.AppendLine($"Generated {generatedDate:yyyy-MM-dd} by `Drps.Diagnostics` (`dotnet run --project Drps.Diagnostics -- regime-distribution`).");
        sb.AppendLine();
        sb.AppendLine(
            "Produced to close the gap flagged by the 2026-07-28 audit: the median/P10/P90/max figures locked " +
            "into CLAUDE.md's \"Regime Thresholds\" and \"Regime Multiplier: Percentile-Anchored Linear Mapping " +
            "Function\" decision blocks (2026-07-26) had no underlying script, data pull, or computation anywhere " +
            "in the repo or its history - they were written directly into decision prose with no traceable " +
            "derivation. This report is that traceable derivation: real data, real computation, re-runnable via " +
            "the command above. It does not modify or replace the locked CLAUDE.md figures - see the comparison " +
            "section at the end, and CLAUDE.md's own dated addendum recording this report's existence.");
        sb.AppendLine();

        sb.AppendLine("## Data sourced");
        sb.AppendLine();
        sb.AppendLine("| Series | Source | Trading days | Date range |");
        sb.AppendLine("|---|---|---|---|");
        AppendCoverageRow(sb, vix);
        AppendCoverageRow(sb, vxn);
        AppendCoverageRow(sb, vix3m);
        sb.AppendLine();
        sb.AppendLine(
            "Sourcing matches CLAUDE.md's locked \"Regime Data Sourcing\" decision (2026-07-26): VIX from Cboe " +
            "direct (sole source, full history); VXN and VIX3M from FRED (`VXNCLS`/`VXVCLS`), the depth source " +
            "for both since FRED's history predates Cboe's own direct CSV export by ~8.6 years (VXN) and ~1.8 " +
            "years (VIX3M).");
        sb.AppendLine();

        sb.AppendLine("## Methodology");
        sb.AppendLine();
        sb.AppendLine(
            "- **Ratio construction:** an inner join on calendar date - a ratio is computed only for a date where " +
            "*both* series have a real published Close. A date present in one series but not the other (a real " +
            "FRED-vs-Cboe publication-calendar mismatch, distinct from FRED's own already-filtered holiday rows) " +
            "is excluded from that ratio's sample entirely, not forward-filled or interpolated.");
        sb.AppendLine(
            "- **Statistics basis:** population statistics (N divisor), not sample statistics (N-1/Bessel's " +
            "correction) - this is a full historical census of every matched trading day, not a sample drawn " +
            "from a larger unobserved population.");
        sb.AppendLine(
            "- **Skewness:** population (Fisher-Pearson g1) skewness - the third standardized moment.");
        sb.AppendLine(
            "- **Percentiles:** linear interpolation between closest ranks (the \"R-7\"/Excel `PERCENTILE.INC` " +
            "method) - the most common default across statistics packages, chosen so this is a reproducibility " +
            "baseline against a well-known method rather than an idiosyncratic one.");
        sb.AppendLine();

        sb.AppendLine("## VXN/VIX ratio");
        sb.AppendLine();
        sb.AppendLine($"Matched sample: **{vxnVixSampleCount}** trading days (dates where both VXN/FRED and VIX/Cboe have a real Close).");
        sb.AppendLine();
        AppendStatisticsSection(sb, vxnVixStats);

        sb.AppendLine("## VIX/VIX3M ratio");
        sb.AppendLine();
        sb.AppendLine($"Matched sample: **{vixVix3mSampleCount}** trading days (dates where both VIX/Cboe and VIX3M/FRED have a real Close).");
        sb.AppendLine();
        AppendStatisticsSection(sb, vixVix3mStats);

        sb.AppendLine("## Comparison against CLAUDE.md's locked figures");
        sb.AppendLine();
        sb.AppendLine(
            $"Comparison bands (stated explicitly, applied mechanically, nothing adjusted or filtered): " +
            $"**match** = within {MatchThresholdPercent}% relative difference; **close** = within " +
            $"{CloseThresholdPercent}% relative difference; **diverges meaningfully** = beyond that. These bands " +
            "are reporting labels only - the raw computed values and raw deltas are given alongside every label " +
            "so the comparison can be judged independently of the label.");
        sb.AppendLine();
        sb.AppendLine("### VXN/VIX");
        sb.AppendLine();
        sb.AppendLine("| Statistic | Locked (CLAUDE.md, 2026-07-26) | Computed (this report) | Delta | Relative | Verdict |");
        sb.AppendLine("|---|---|---|---|---|---|");
        AppendComparisonRow(sb, "Median", 1.2111, vxnVixStats.Median);
        AppendComparisonRow(sb, "P90", 1.5237, vxnVixStats.Percentiles[90]);
        AppendComparisonRow(sb, "P10", 1.0269, vxnVixStats.Percentiles[10]);
        AppendComparisonRow(sb, "Max", 2.7337, vxnVixStats.Max);
        sb.AppendLine();
        sb.AppendLine(
            $"Locked max date: 2001 (year only, per CLAUDE.md's own prose - no exact date was ever locked). " +
            $"Computed max date: **{vxnVixStats.MaxDate:yyyy-MM-dd}**.");
        sb.AppendLine();

        sb.AppendLine("### VIX/VIX3M");
        sb.AppendLine();
        sb.AppendLine("| Statistic | Locked (CLAUDE.md, 2026-07-26) | Computed (this report) | Delta | Relative | Verdict |");
        sb.AppendLine("|---|---|---|---|---|---|");
        AppendComparisonRow(sb, "Median", 0.8919, vixVix3mStats.Median);
        AppendComparisonRow(sb, "P90", 1.0012, vixVix3mStats.Percentiles[90]);
        AppendComparisonRow(sb, "P10", 0.8150, vixVix3mStats.Percentiles[10]);
        AppendComparisonRow(sb, "Max", 1.4309, vixVix3mStats.Max);
        sb.AppendLine();
        sb.AppendLine(
            $"Locked max date: 2008-10-24 (GFC), per CLAUDE.md's own prose. Computed max date: " +
            $"**{vixVix3mStats.MaxDate:yyyy-MM-dd}**.");
        sb.AppendLine();

        sb.AppendLine("## Mean and skewness (not present anywhere in the locked CLAUDE.md figures)");
        sb.AppendLine();
        sb.AppendLine(
            "The 2026-07-28 audit noted mean and skewness were never computed or recorded for either ratio - " +
            "only median/P10/P90/max made it into CLAUDE.md's prose. This report closes that gap; there is no " +
            "locked figure to compare either value against, so these are reported standalone.");
        sb.AppendLine();
        sb.AppendLine("| Ratio | Mean | Skewness |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| VXN/VIX | {vxnVixStats.Mean.ToString("F4", CultureInfo.InvariantCulture)} | {vxnVixStats.Skewness.ToString("F4", CultureInfo.InvariantCulture)} |");
        sb.AppendLine($"| VIX/VIX3M | {vixVix3mStats.Mean.ToString("F4", CultureInfo.InvariantCulture)} | {vixVix3mStats.Skewness.ToString("F4", CultureInfo.InvariantCulture)} |");
        sb.AppendLine();

        return sb.ToString();
    }

    private static void AppendCoverageRow(StringBuilder sb, SeriesCoverage coverage)
    {
        sb.AppendLine(
            $"| {coverage.Label} | {coverage.SourceDescription} | {coverage.Count} | " +
            $"{coverage.FirstDate:yyyy-MM-dd} through {coverage.LastDate:yyyy-MM-dd} |");
    }

    private static void AppendStatisticsSection(StringBuilder sb, DistributionStatistics stats)
    {
        sb.AppendLine("| Statistic | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Count | {stats.Count} |");
        sb.AppendLine($"| Mean | {stats.Mean.ToString("F4", CultureInfo.InvariantCulture)} |");
        sb.AppendLine($"| Median | {stats.Median.ToString("F4", CultureInfo.InvariantCulture)} |");
        sb.AppendLine($"| Standard deviation | {stats.StandardDeviation.ToString("F4", CultureInfo.InvariantCulture)} |");
        sb.AppendLine($"| Skewness | {stats.Skewness.ToString("F4", CultureInfo.InvariantCulture)} |");
        sb.AppendLine($"| Min | {stats.Min.ToString("F4", CultureInfo.InvariantCulture)} ({stats.MinDate:yyyy-MM-dd}) |");
        sb.AppendLine($"| Max | {stats.Max.ToString("F4", CultureInfo.InvariantCulture)} ({stats.MaxDate:yyyy-MM-dd}) |");
        sb.AppendLine();
        sb.AppendLine("| Percentile | Value |");
        sb.AppendLine("|---|---|");
        foreach (var p in new[] { 10, 25, 50, 75, 90, 95, 99 })
            sb.AppendLine($"| P{p} | {stats.Percentiles[p].ToString("F4", CultureInfo.InvariantCulture)} |");
        sb.AppendLine();
    }

    private static void AppendComparisonRow(StringBuilder sb, string statisticName, double locked, double computed)
    {
        var delta = computed - locked;
        var relativePercent = locked == 0 ? double.NaN : Math.Abs(delta) / Math.Abs(locked) * 100.0;

        var verdict = double.IsNaN(relativePercent)
            ? "N/A (locked value is zero)"
            : relativePercent <= MatchThresholdPercent ? "MATCH"
            : relativePercent <= CloseThresholdPercent ? "CLOSE"
            : "DIVERGES MEANINGFULLY";

        var relativeText = double.IsNaN(relativePercent) ? "N/A" : $"{relativePercent.ToString("F2", CultureInfo.InvariantCulture)}%";

        // Sign built manually rather than via a "+0.0000;-0.0000" two-section custom format
        // string - that produced a genuine double-sign display bug ("-+0.0000") for deltas
        // that are a tiny negative float (binary rounding noise from the percentile
        // interpolation, e.g. -1e-16) which round to "0.0000" at 4 decimals but still trip the
        // format's negative-section selection. Explicit sign + Math.Abs sidesteps the whole
        // ambiguity rather than chasing the exact custom-format edge case.
        var deltaText = $"{(delta < 0 ? "-" : "+")}{Math.Abs(delta).ToString("F4", CultureInfo.InvariantCulture)}";

        sb.AppendLine(
            $"| {statisticName} | {locked.ToString("F4", CultureInfo.InvariantCulture)} | " +
            $"{computed.ToString("F4", CultureInfo.InvariantCulture)} | " +
            $"{deltaText} | {relativeText} | **{verdict}** |");
    }
}
