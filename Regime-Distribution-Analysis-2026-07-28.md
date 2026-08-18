# Regime Distribution Analysis: VXN/VIX and VIX/VIX3M (empirical, reproducible)

Generated 2026-07-28 by `Drps.Diagnostics` (`dotnet run --project Drps.Diagnostics -- regime-distribution`).

Produced to close the gap flagged by the 2026-07-28 audit: the median/P10/P90/max figures locked into CLAUDE.md's "Regime Thresholds" and "Regime Multiplier: Percentile-Anchored Linear Mapping Function" decision blocks (2026-07-26) had no underlying script, data pull, or computation anywhere in the repo or its history - they were written directly into decision prose with no traceable derivation. This report is that traceable derivation: real data, real computation, re-runnable via the command above. It does not modify or replace the locked CLAUDE.md figures - see the comparison section at the end, and CLAUDE.md's own dated addendum recording this report's existence.

## Data sourced

| Series | Source | Trading days | Date range |
|---|---|---|---|
| VIX | Cboe direct | 9237 | 1990-01-02 through 2026-07-27 |
| VXN | FRED (VXNCLS) | 6407 | 2001-02-02 through 2026-07-27 |
| VIX3M | FRED (VXVCLS) | 4689 | 2007-12-04 through 2026-07-27 |

Sourcing matches CLAUDE.md's locked "Regime Data Sourcing" decision (2026-07-26): VIX from Cboe direct (sole source, full history); VXN and VIX3M from FRED (`VXNCLS`/`VXVCLS`), the depth source for both since FRED's history predates Cboe's own direct CSV export by ~8.6 years (VXN) and ~1.8 years (VIX3M).

## Methodology

- **Ratio construction:** an inner join on calendar date - a ratio is computed only for a date where *both* series have a real published Close. A date present in one series but not the other (a real FRED-vs-Cboe publication-calendar mismatch, distinct from FRED's own already-filtered holiday rows) is excluded from that ratio's sample entirely, not forward-filled or interpolated.
- **Statistics basis:** population statistics (N divisor), not sample statistics (N-1/Bessel's correction) - this is a full historical census of every matched trading day, not a sample drawn from a larger unobserved population.
- **Skewness:** population (Fisher-Pearson g1) skewness - the third standardized moment.
- **Percentiles:** linear interpolation between closest ranks (the "R-7"/Excel `PERCENTILE.INC` method) - the most common default across statistics packages, chosen so this is a reproducibility baseline against a well-known method rather than an idiosyncratic one.

## VXN/VIX ratio

Matched sample: **6404** trading days (dates where both VXN/FRED and VIX/Cboe have a real Close).

| Statistic | Value |
|---|---|
| Count | 6404 |
| Mean | 1.2629 |
| Median | 1.2112 |
| Standard deviation | 0.2566 |
| Skewness | 2.1219 |
| Min | 0.7560 (2025-04-08) |
| Max | 2.7337 (2001-02-13) |

| Percentile | Value |
|---|---|
| P10 | 1.0269 |
| P25 | 1.0914 |
| P50 | 1.2112 |
| P75 | 1.3401 |
| P90 | 1.5241 |
| P95 | 1.7804 |
| P99 | 2.3108 |

## VIX/VIX3M ratio

Matched sample: **4689** trading days (dates where both VIX/Cboe and VIX3M/FRED have a real Close).

| Statistic | Value |
|---|---|
| Count | 4689 |
| Mean | 0.9042 |
| Median | 0.8920 |
| Standard deviation | 0.0819 |
| Skewness | 1.3926 |
| Min | 0.7104 (2012-03-16) |
| Max | 1.4309 (2008-10-24) |

| Percentile | Value |
|---|---|
| P10 | 0.8150 |
| P25 | 0.8483 |
| P50 | 0.8920 |
| P75 | 0.9476 |
| P90 | 1.0012 |
| P95 | 1.0371 |
| P99 | 1.1928 |

## Comparison against CLAUDE.md's locked figures

Comparison bands (stated explicitly, applied mechanically, nothing adjusted or filtered): **match** = within 0.5% relative difference; **close** = within 5% relative difference; **diverges meaningfully** = beyond that. These bands are reporting labels only - the raw computed values and raw deltas are given alongside every label so the comparison can be judged independently of the label.

### VXN/VIX

| Statistic | Locked (CLAUDE.md, 2026-07-26) | Computed (this report) | Delta | Relative | Verdict |
|---|---|---|---|---|---|
| Median | 1.2111 | 1.2112 | +0.0001 | 0.00% | **MATCH** |
| P90 | 1.5237 | 1.5241 | +0.0004 | 0.03% | **MATCH** |
| P10 | 1.0269 | 1.0269 | -0.0000 | 0.00% | **MATCH** |
| Max | 2.7337 | 2.7337 | +0.0000 | 0.00% | **MATCH** |

Locked max date: 2001 (year only, per CLAUDE.md's own prose - no exact date was ever locked). Computed max date: **2001-02-13**.

### VIX/VIX3M

| Statistic | Locked (CLAUDE.md, 2026-07-26) | Computed (this report) | Delta | Relative | Verdict |
|---|---|---|---|---|---|
| Median | 0.8919 | 0.8920 | +0.0001 | 0.01% | **MATCH** |
| P90 | 1.0012 | 1.0012 | +0.0000 | 0.00% | **MATCH** |
| P10 | 0.8150 | 0.8150 | -0.0000 | 0.00% | **MATCH** |
| Max | 1.4309 | 1.4309 | +0.0000 | 0.00% | **MATCH** |

Locked max date: 2008-10-24 (GFC), per CLAUDE.md's own prose. Computed max date: **2008-10-24**.

## Mean and skewness (not present anywhere in the locked CLAUDE.md figures)

The 2026-07-28 audit noted mean and skewness were never computed or recorded for either ratio - only median/P10/P90/max made it into CLAUDE.md's prose. This report closes that gap; there is no locked figure to compare either value against, so these are reported standalone.

| Ratio | Mean | Skewness |
|---|---|---|
| VXN/VIX | 1.2629 | 2.1219 |
| VIX/VIX3M | 0.9042 | 1.3926 |

