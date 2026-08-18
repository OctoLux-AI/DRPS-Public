using Drps.Ingestion.Persistence;
using Drps.Shared.Helpers;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Ingestion.Orchestration;

/// <summary>
/// Ported from CapitalFill's Solon.Shared.Services.TickerUniverseService (Solonosphere repo) -
/// reuses the same NasdaqTrader SymDir source, single-HttpClient fetch shape, and
/// ParseTickers logic as-is, per CLAUDE.md's "Universe Source and Persistence" decision.
///
/// Two deliberate divergences from the ported source, both required by this codebase's own
/// rules rather than oversights:
/// 1. Persistence is an append-only dated snapshot (<see cref="UniverseSnapshot"/>), not
///    CapitalFill's upsert-in-place BulkUpsertTickerUniverseAsync - see UniverseSnapshot's own
///    doc comment for why. The "cache check" below (does today's snapshot already exist) is
///    this design's equivalent of CapitalFill's 24-hour TTL check.
/// 2. Fail-CLOSED on a total fetch failure. CapitalFill fails open and keeps serving whatever
///    was already cached; CLAUDE.md's staleness-TTL section is explicit that stale-or-missing
///    data must block, never pass silently - so when both sources fail, this leaves that
///    date's snapshot absent entirely rather than persisting a partial or empty one. A
///    single-source failure is NOT this case: CapitalFill's own "one source's failure doesn't
///    block the other" behavior is preserved, and a partial (single-source) snapshot is still
///    written for that date.
///
/// No PushoverAlertService call - Pushover remains parked in this codebase (CLAUDE.md's Known
/// Open Questions), unrelated to this task's scope.
/// </summary>
public class UniverseIngestionRunner
{
    public const string HttpClientName = "NasdaqTraderUniverse";

    // Identifies this Runner's rows in WorkerRunRecord - see WorkerRunGuard's own doc comment.
    // Distinct from Worker.cs's "Drps.Ingestion" and WeeklyVarianceAuditWorker's
    // "Drps.WeeklyVarianceAudit" so none of the three collide in the same shared table.
    private const string WorkerName = "Drps.UniverseSnapshot";

    private const string NasdaqUrl = "https://www.nasdaqtrader.com/dynamic/SymDir/nasdaqlisted.txt";
    private const string OtherUrl = "https://www.nasdaqtrader.com/dynamic/SymDir/otherlisted.txt";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DrpsDbContext _dbContext;
    private readonly ILogger<UniverseIngestionRunner> _logger;

    public UniverseIngestionRunner(IHttpClientFactory httpClientFactory, DrpsDbContext dbContext, ILogger<UniverseIngestionRunner> logger)
    {
        _httpClientFactory = httpClientFactory;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task RunAsync(DateOnly snapshotDate, CancellationToken cancellationToken)
    {
        // Idempotency guard - standardized onto WorkerRunGuard (CLAUDE.md's guard-
        // standardization task, 2026-07-24), replacing the ad hoc "does a snapshot row already
        // exist for this date" check this class used to run inline. Same fail-open shape as
        // Worker.cs's own guard check: a guard-check failure must never itself become a reason
        // a legitimate run is blocked.
        var alreadyRanToday = false;
        try
        {
            alreadyRanToday = await WorkerRunGuard.HasRunTodayAsync(_dbContext, WorkerName, snapshotDate, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[UNIVERSE-RUNNER]: idempotency guard check failed for {SnapshotDate} - proceeding as if not yet run " +
                "today (fail-open)",
                snapshotDate);
        }

        if (alreadyRanToday)
        {
            _logger.LogInformation(
                "[UNIVERSE-RUNNER]: already ran successfully for {SnapshotDate} - skipping fetch", snapshotDate);
            return;
        }

        List<string>? nasdaqTickers;
        List<string>? otherTickers;

        try
        {
            nasdaqTickers = await FetchTickersAsync(NasdaqUrl, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A real shutdown request, not a source failure - must propagate, not be
            // swallowed into "this source failed."
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[UNIVERSE-RUNNER]: NASDAQ fetch failed for {SnapshotDate}", snapshotDate);
            nasdaqTickers = null;
        }

        try
        {
            otherTickers = await FetchTickersAsync(OtherUrl, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[UNIVERSE-RUNNER]: NYSE/AMEX fetch failed for {SnapshotDate}", snapshotDate);
            otherTickers = null;
        }

        // Explicit, queryable visibility into which source(s) actually succeeded for this date -
        // CLAUDE.md's fail-closed-everywhere principle: a downstream consumer reading this
        // date's UniverseSnapshot ticker rows alone has no way to tell a normal, fully-covered
        // day apart from one where an entire exchange's tickers are silently absent. Recorded
        // regardless of outcome (including the both-failed case below) so this table is a
        // complete, honest record of every fetch attempt - never itself silently missing for a
        // date that was actually attempted. See UniverseSnapshotCoverage's own doc comment.
        var sourcesSucceeded = UniverseSourceCoverage.None;
        if (nasdaqTickers is not null && nasdaqTickers.Count > 0)
            sourcesSucceeded |= UniverseSourceCoverage.Nasdaq;
        if (otherTickers is not null && otherTickers.Count > 0)
            sourcesSucceeded |= UniverseSourceCoverage.NyseAmex;

        _dbContext.UniverseSnapshotCoverages.Add(new UniverseSnapshotCoverage
        {
            SnapshotDate = snapshotDate,
            SourcesSucceeded = sourcesSucceeded,
            RecordedAt = DateTimeOffset.UtcNow
        });

        if ((nasdaqTickers is null || nasdaqTickers.Count == 0) && (otherTickers is null || otherTickers.Count == 0))
        {
            // Fail-closed: no ticker row is written for this date at all, rather than a partial
            // or empty snapshot silently standing in for a real one. Deliberate divergence from
            // CapitalFill's fail-open "stale cache preserved" behavior - see this class's own
            // doc comment. The coverage row above (SourcesSucceeded = None) is still saved, so
            // this date reads as "attempted, both sources failed," not "never attempted at all."
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogWarning(
                "[UNIVERSE-RUNNER]: both NASDAQ and NYSE/AMEX fetches failed for {SnapshotDate} - " +
                "leaving that date's snapshot absent rather than persisting an incomplete universe",
                snapshotDate);

            // Still records success for the guard - the cycle reached its end (a fetch was
            // genuinely attempted and the outcome recorded via the coverage row above), same
            // "ran to completion regardless of per-item outcome" interpretation Worker.cs's own
            // guard uses for per-symbol failures. Prevents a same-day re-trigger from re-hitting
            // NasdaqTrader again for a day already known to have failed.
            await RecordSuccessAsync(snapshotDate, cancellationToken);
            return;
        }

        var nasdaqCount = nasdaqTickers?.Count ?? 0;
        var otherCount = otherTickers?.Count ?? 0;

        var rows = new List<UniverseSnapshot>(nasdaqCount + otherCount);
        if (nasdaqTickers is not null)
            foreach (var ticker in nasdaqTickers)
                rows.Add(new UniverseSnapshot { SnapshotDate = snapshotDate, Ticker = ticker, Exchange = "NASDAQ" });
        if (otherTickers is not null)
            foreach (var ticker in otherTickers)
                rows.Add(new UniverseSnapshot { SnapshotDate = snapshotDate, Ticker = ticker, Exchange = "NYSE" });

        _dbContext.UniverseSnapshots.AddRange(rows);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var partialNote = nasdaqTickers is null || otherTickers is null ? " (partial - one source failed)" : "";
        _logger.LogInformation(
            "[UNIVERSE-RUNNER]: snapshot {SnapshotDate} written - {NasdaqCount} NASDAQ + {OtherCount} NYSE/AMEX = {Total} total symbols{PartialNote}",
            snapshotDate, nasdaqCount, otherCount, rows.Count, partialNote);

        await RecordSuccessAsync(snapshotDate, cancellationToken);
    }

    // Shared by both completion paths above (both-sources-failed and the normal write path) -
    // one implementation of "how a successful run gets recorded," same fail-open shape as
    // Worker.cs's own guard-recording call site. A failure here only means a future same-day
    // restart won't be recognized as a re-fire; it does not affect today's already-completed work.
    private async Task RecordSuccessAsync(DateOnly snapshotDate, CancellationToken cancellationToken)
    {
        try
        {
            await WorkerRunGuard.RecordSuccessfulRunAsync(_dbContext, WorkerName, snapshotDate, DateTime.Now, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[UNIVERSE-RUNNER]: failed to record successful run for {SnapshotDate} - the idempotency guard will " +
                "not recognize today's run as already complete if this runs again today",
                snapshotDate);
        }
    }

    private async Task<List<string>> FetchTickersAsync(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var content = await client.GetStringAsync(url, cancellationToken);
        return ParseTickers(content);
    }

    // Ported verbatim from CapitalFill's TickerUniverseService.ParseTickers. Note: the
    // "Symbol" header-skip only literally matches nasdaqlisted.txt's header row
    // ("Symbol|..."); otherlisted.txt's header starts with "ACT Symbol|...", which this check
    // does not match. Confirmed harmless, not a latent bug worth "fixing" in an as-is port:
    // "ACT Symbol" fails TickerSymbolHelper.IsValidSymbol on both length (>5 chars) and the
    // all-letters check (contains a space), so it's filtered out by the validation step
    // regardless. CapitalFill's own behavior already relies on this incidental filtering, so
    // this port preserves it exactly rather than "improving" logic this task did not ask to
    // change.
    private static List<string> ParseTickers(string content)
    {
        var result = new List<string>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("Symbol", StringComparison.Ordinal)) continue;
            if (line.StartsWith("File Creation Time", StringComparison.Ordinal)) continue;

            var pipe = line.IndexOf('|');
            var ticker = (pipe >= 0 ? line[..pipe] : line).Trim().ToUpperInvariant();

            if (TickerSymbolHelper.IsValidSymbol(ticker))
                result.Add(ticker);
        }
        return result;
    }
}
