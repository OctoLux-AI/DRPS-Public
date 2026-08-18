using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Ingestion.Orchestration;

public class BarReconciliationService
{
    private const string Resolution = "1Day";
    private const decimal Tolerance = 0.001m;

    // Derived from real 2026-07-12 basket-test data (50-symbol diverse basket, then a
    // dedicated 10-symbol low-price/control basket): Alpaca vs. Tiingo showed a consistent
    // absolute Close-price disagreement of roughly $0.005-$0.10, independent of share price
    // (e.g. JPM: $0.29 diff at ~$335 stayed within the 0.1% tolerance; PLUG: a mere $0.01
    // diff at ~$2.45 failed it). A bar passes if it satisfies the percentage tolerance OR
    // this absolute floor, whichever is looser. Starting point, subject to recalibration.
    private const decimal AbsoluteToleranceFloor = 0.02m;

    private const int VerificationFloor = 2;

    private readonly DrpsDbContext _dbContext;
    private readonly ILogger<BarReconciliationService> _logger;

    public BarReconciliationService(DrpsDbContext dbContext, ILogger<BarReconciliationService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task ReconcileAsync(string symbol, DateOnly start, DateOnly end, int computationVersion, CancellationToken cancellationToken)
    {
        var startTimestamp = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var endTimestamp = new DateTimeOffset(end.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var bars = await _dbContext.RawOhlcvBars
            .Where(b => b.Symbol == symbol
                && b.Resolution == Resolution
                && b.Timestamp >= startTimestamp
                && b.Timestamp <= endTimestamp)
            .ToListAsync(cancellationToken);

        var groups = bars.GroupBy(b => b.Timestamp);

        var verifiedCount = 0;
        var discrepancyCount = 0;
        var groupCount = 0;

        foreach (var group in groups)
        {
            var timestamp = group.Key;

            // A real equity quote is never zero or negative — treat any such bar as
            // corrupt/invalid data from a feeder, not a legitimate observation. Excluded
            // before any comparison logic runs so IsWithinTolerance/PercentDiff never divide
            // by zero.
            var barsInGroup = new List<RawOhlcvBar>();
            foreach (var bar in group)
            {
                if (bar.Close <= 0m)
                {
                    _logger.LogWarning(
                        "[BAR-RECONCILIATION]: excluding invalid bar {Source}/{Symbol}/{Timestamp}: Close={Close}",
                        bar.Source, symbol, timestamp, bar.Close);
                    continue;
                }

                barsInGroup.Add(bar);
            }

            if (barsInGroup.Count == 0)
            {
                _logger.LogWarning(
                    "[BAR-RECONCILIATION]: all bars for {Symbol}/{Timestamp} were invalid, skipping reconciliation for this date",
                    symbol, timestamp);
                continue;
            }

            // Raw bars are append-only: a source re-ingested across separate runs can have
            // multiple historical rows for the same (Symbol, Timestamp, Resolution). Only the
            // most recently ingested row per source is a live comparison candidate — otherwise
            // the pairwise loop below compares the same source pair once per duplicate row.
            barsInGroup = barsInGroup
                .GroupBy(b => b.Source)
                .Select(g => g.OrderByDescending(b => b.IngestedAt).ThenByDescending(b => b.Id).First())
                .ToList();

            groupCount++;
            var primaryBar = barsInGroup.FirstOrDefault(b => b.Source == SourceType.Alpaca);

            int matchedCount;
            if (primaryBar is not null)
            {
                matchedCount = barsInGroup.Count(b => IsWithinTolerance(primaryBar.Close, b.Close));
            }
            else
            {
                // No primary: fall back to the largest mutually-agreeing cluster, kept simple
                // via pairwise comparison rather than true transitive clustering — for exactly
                // two disagreeing sources this correctly yields 1, not 2.
                matchedCount = barsInGroup.Max(candidate => barsInGroup.Count(b => IsWithinTolerance(candidate.Close, b.Close)));
            }

            var sourceCount = barsInGroup.Select(b => b.Source).Distinct().Count();
            // matchedCount alone isn't sufficient: duplicate rows from the same source
            // (e.g. a re-run appending another Alpaca bar for the same day) can reach the
            // floor without a second independent source ever reporting. The n=2 floor is a
            // cross-source requirement, not just an agreeing-row-count requirement.
            var verified = sourceCount >= VerificationFloor && matchedCount >= VerificationFloor;
            if (verified)
            {
                verifiedCount++;
            }

            var barVerification = await _dbContext.BarVerifications
                .SingleOrDefaultAsync(
                    v => v.Symbol == symbol && v.Timestamp == timestamp && v.Resolution == Resolution,
                    cancellationToken);

            if (barVerification is null)
            {
                barVerification = new BarVerification
                {
                    Symbol = symbol,
                    Timestamp = timestamp,
                    Resolution = Resolution
                };
                _dbContext.BarVerifications.Add(barVerification);
            }

            barVerification.SourceCount = sourceCount;
            barVerification.MatchedSourceCount = matchedCount;
            barVerification.Verified = verified;
            barVerification.ToleranceApplied = Tolerance;
            barVerification.PrimarySourceValue = primaryBar?.Close;
            barVerification.ComputationVersion = computationVersion;
            barVerification.EvaluatedAt = DateTimeOffset.UtcNow;

            for (var i = 0; i < barsInGroup.Count; i++)
            {
                for (var j = i + 1; j < barsInGroup.Count; j++)
                {
                    var a = barsInGroup[i];
                    var b = barsInGroup[j];

                    if (IsWithinTolerance(a.Close, b.Close))
                        continue;

                    discrepancyCount++;

                    // Narrow, evidence-scoped exception - see CLAUDE.md, "Reconciliation:
                    // Narrow Tiingo-Close Exception for the OHL-Agrees/C-Disagrees Signature"
                    // (2026-07-17). This is NOT a general Alpaca-vs-Tiingo preference -
                    // BarReconciliationService's standing rule (Alpaca is primary/ground truth
                    // when present) is unchanged for every other disagreement shape, including
                    // any future third source. It fires only when the disagreeing pair is
                    // exactly Alpaca+Tiingo AND every one of Open/High/Low independently
                    // agrees within the same hybrid tolerance used for Close - the specific
                    // signature a multi-ticker investigation (JPM, AAPL, BAC, KO, UNH, UPS,
                    // SIRI) confirmed against independently-published closes as Alpaca's
                    // feed=iex tape missing the true close on a volatile session, not a
                    // wrong-ticker mapping or a genuine data-quality problem on either side.
                    var resolutionMethod = DiscrepancyResolutionMethod.Unresolved;

                    var isAlpacaTiingoPair =
                        (a.Source == SourceType.Alpaca && b.Source == SourceType.Tiingo) ||
                        (a.Source == SourceType.Tiingo && b.Source == SourceType.Alpaca);

                    if (isAlpacaTiingoPair
                        && IsWithinTolerance(a.Open, b.Open)
                        && IsWithinTolerance(a.High, b.High)
                        && IsWithinTolerance(a.Low, b.Low))
                    {
                        resolutionMethod = DiscrepancyResolutionMethod.OhlAgreedCloseResolvedToTiingo;

                        var tiingoBar = a.Source == SourceType.Tiingo ? a : b;
                        if (!barVerification.Verified)
                        {
                            barVerification.Verified = true;
                            verifiedCount++;
                        }

                        barVerification.PrimarySourceValue = tiingoBar.Close;
                    }

                    _dbContext.Discrepancies.Add(new Discrepancy
                    {
                        Symbol = symbol,
                        Timestamp = timestamp,
                        FieldOrBarType = "Close",
                        SourceA = a.Source,
                        ValueA = a.Close,
                        SourceB = b.Source,
                        ValueB = b.Close,
                        PercentDiff = Math.Abs(a.Close - b.Close) / a.Close,
                        ResolutionMethod = resolutionMethod,
                        DetectedAt = DateTimeOffset.UtcNow
                    });
                }
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "[BAR-RECONCILIATION]: {Symbol} {Start}-{End}: {GroupCount} day(s) evaluated, {VerifiedCount} verified, {DiscrepancyCount} discrepancy row(s) logged",
            symbol, start, end, groupCount, verifiedCount, discrepancyCount);
    }

    private static bool IsWithinTolerance(decimal reference, decimal other)
    {
        var absoluteDiff = Math.Abs(reference - other);
        return absoluteDiff / reference <= Tolerance || absoluteDiff <= AbsoluteToleranceFloor;
    }
}
