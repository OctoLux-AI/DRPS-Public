using Drps.Ingestion.Feeders;
using Drps.Ingestion.Orchestration;
using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Calculator.Verification;

/// <summary>
/// Runs dual-source (Alpaca+Tiingo) bar ingestion and verification for an arbitrary,
/// dynamically-supplied ticker list - e.g. a set of discovered candidates that need their
/// own bars verified on demand, distinct from Drps.Ingestion/Worker.cs's static 40-symbol
/// watchlist loop, which is the only caller of this same underlying logic today. Reuses
/// Drps.Ingestion's existing IngestionRunner/BarReconciliationService directly rather than
/// duplicating that fetch/reconcile logic here - Drps.Calculator.csproj already references
/// Drps.Ingestion.csproj (see the .csproj's own comment), so no new project reference or new
/// ingestion/reconciliation code was needed to add this.
///
/// Deliberately orchestrates IngestionRunner + BarReconciliationService directly rather than
/// via IngestionJob (2026-08-06 audit fix, CLAUDE.md's "Eliminate the redundant Alpaca API
/// call" decision) - IngestionJob's own IngestionRunner is resolved once via DI with every
/// registered IMarketDataFeeder baked in permanently, and IngestionRunner.RunAsync has no
/// per-call way to run against a subset of feeders. Skipping the Alpaca leg conditionally
/// (see VerifyCandidatesAsync's own doc comment) requires constructing a fresh IngestionRunner
/// per ticker with the exact feeder set that call actually needs - IngestionRunner's own
/// fetch/reconcile logic is still fully reused, unmodified, just instantiated differently.
///
/// Deliberately separate from RollingDmaStateService, which stays focused on alignment
/// computation only, never verification.
/// </summary>
public class CandidateBarVerificationService
{
    private const string Resolution = "1Day";

    // Calendar-day (not trading-day) lookback applied to the supplied asOfDate to build the
    // [start, end] range this service fetches/reconciles - wide enough to comfortably cover a
    // weekend/holiday gap around Gate's own DMA-5 Tier 1 window (CLAUDE.md's "DMA
    // Verification: Blast-Radius Scoping", 2026-08-04) without pulling the full multi-month
    // history a fresh bootstrap would need.
    private const int LookbackDays = 10;

    // Recency tolerance for the Alpaca-skip check below (2026-08-06 audit fix) - deliberately
    // NOT an exact trading-calendar computation. Pulling in ITradingCalendarService (a live
    // Alpaca calendar-endpoint call) to decide whether to skip a bars fetch would partially
    // defeat the point of reducing Alpaca API volume, and would itself need the same
    // fail-open-to-a-live-fetch treatment on failure. A normal weekend gap is 2 calendar days;
    // 3 gives a small buffer without meaningfully weakening the check. A market holiday can
    // occasionally cause an unnecessary-but-safe live fetch when otherwise-sufficient coverage
    // exists - the safe direction, per this method's own fail-open requirement (see point 2 of
    // the CLAUDE.md decision this closes).
    private const int AlpacaSufficiencyRecencyDays = 3;

    private readonly IEnumerable<IMarketDataFeeder> _feeders;
    private readonly DrpsDbContext _dbContext;
    private readonly SourceStatusTracker _sourceStatusTracker;
    private readonly ILogger<IngestionRunner> _ingestionRunnerLogger;
    private readonly BarReconciliationService _barReconciliationService;
    private readonly ILogger<CandidateBarVerificationService> _logger;

    public CandidateBarVerificationService(
        IEnumerable<IMarketDataFeeder> feeders,
        DrpsDbContext dbContext,
        SourceStatusTracker sourceStatusTracker,
        ILogger<IngestionRunner> ingestionRunnerLogger,
        BarReconciliationService barReconciliationService,
        ILogger<CandidateBarVerificationService> logger)
    {
        _feeders = feeders;
        _dbContext = dbContext;
        _sourceStatusTracker = sourceStatusTracker;
        _ingestionRunnerLogger = ingestionRunnerLogger;
        _barReconciliationService = barReconciliationService;
        _logger = logger;
    }

    /// <summary>
    /// For each supplied ticker: fetches bars for [asOfDate - LookbackDays, asOfDate] and
    /// reconciles them into BarVerifications (BarReconciliationService) - applied to a
    /// caller-supplied candidate list instead of the static watchlist. Runs unconditionally
    /// regardless of outcome: a contrary/failed verification (sources disagree, Verified=false)
    /// is written and logged like any other reconciliation result, never suppressed. Isolates
    /// each ticker's own unexpected exception (e.g. a DB failure) so one bad ticker never
    /// blocks the rest of the list - matching Worker.cs's own per-symbol isolation convention.
    /// An empty (or null) ticker list is a no-op - no feeder is ever called.
    ///
    /// Alpaca-fetch skip (2026-08-06 audit fix, CLAUDE.md's "Eliminate the redundant Alpaca API
    /// call in CandidateBarVerificationService"): Drps.Ingestion's UniverseBarSweepRunner
    /// already writes Alpaca bars for the full universe every night, scheduled ahead of
    /// Calculator's own run - by the time this method runs, a fresh Alpaca row for a given
    /// candidate typically already exists. Before fetching, each ticker's existing RawOhlcvBars
    /// (Source=Alpaca) are checked for recency; if a sufficiently recent row already exists
    /// (see AlpacaSufficiencyRecencyDays), the live Alpaca fetch is skipped entirely for that
    /// ticker and reconciliation proceeds against the existing row(s). If no sufficiently
    /// recent Alpaca row exists - the sweep hasn't reached this ticker yet, this is being run
    /// out-of-band, or coverage is genuinely stale/incomplete - this falls back to a live
    /// Alpaca fetch, same as before this fix (fail-open, never fail-closed on a missing/stale
    /// row). Tiingo is never skipped: nothing else in the pipeline fetches Tiingo for these
    /// tickers, so every candidate gets a live Tiingo fetch unconditionally, exactly as before
    /// this fix.
    /// </summary>
    public async Task VerifyCandidatesAsync(
        IEnumerable<string> tickers, DateOnly asOfDate, CancellationToken cancellationToken)
    {
        var tickerList = tickers?.ToList() ?? [];
        if (tickerList.Count == 0)
        {
            _logger.LogInformation(
                "[CANDIDATE-BAR-VERIFICATION]: no candidate tickers supplied for {AsOfDate} - nothing to verify",
                asOfDate);
            return;
        }

        var start = asOfDate.AddDays(-LookbackDays);
        var end = asOfDate;

        var processedCount = 0;
        var failedCount = 0;
        var alpacaSkippedCount = 0;

        foreach (var ticker in tickerList)
        {
            try
            {
                var alpacaCoverageSufficient = await HasSufficientAlpacaCoverageAsync(ticker, start, end, cancellationToken);

                var feedersToUse = alpacaCoverageSufficient
                    ? _feeders.Where(f => f.Source != SourceType.Alpaca)
                    : _feeders;

                if (alpacaCoverageSufficient)
                {
                    alpacaSkippedCount++;
                    _logger.LogInformation(
                        "[CANDIDATE-BAR-VERIFICATION]: {Ticker}: existing Alpaca bar(s) already cover {Start}-{End} " +
                        "within the {RecencyDays}-day recency window - skipping live Alpaca fetch",
                        ticker, start, end, AlpacaSufficiencyRecencyDays);
                }

                // A fresh IngestionRunner per ticker, scoped to exactly the feeders this call
                // needs - see this class's own doc comment for why the DI-registered
                // IngestionRunner/IngestionJob can't be reused here.
                var runner = new IngestionRunner(feedersToUse, _dbContext, _sourceStatusTracker, _ingestionRunnerLogger);
                await runner.RunAsync(ticker, start, end, cancellationToken);
                await _barReconciliationService.ReconcileAsync(ticker, start, end, computationVersion: 1, cancellationToken);

                processedCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A real shutdown request, not a verification failure - must propagate, same
                // as every other per-item loop in this codebase (e.g. Worker.cs's own
                // watchlist loop).
                throw;
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(ex,
                    "[CANDIDATE-BAR-VERIFICATION]: unexpected exception verifying {Ticker} for {Start}-{End}",
                    ticker, start, end);
            }
        }

        _logger.LogInformation(
            "[CANDIDATE-BAR-VERIFICATION]: {AsOfDate}: {ProcessedCount} of {TotalCount} candidate ticker(s) " +
            "processed, {FailedCount} unexpected failure(s), {AlpacaSkippedCount} live Alpaca fetch(es) skipped " +
            "(existing coverage)",
            asOfDate, processedCount, tickerList.Count, failedCount, alpacaSkippedCount);
    }

    private async Task<bool> HasSufficientAlpacaCoverageAsync(
        string ticker, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        var startTimestamp = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var endTimestamp = new DateTimeOffset(end.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var recencyThreshold = new DateTimeOffset(
            end.AddDays(-AlpacaSufficiencyRecencyDays).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        return await _dbContext.RawOhlcvBars.AnyAsync(
            b => b.Symbol == ticker
                && b.Source == SourceType.Alpaca
                && b.Resolution == Resolution
                && b.Timestamp >= startTimestamp
                && b.Timestamp <= endTimestamp
                && b.Timestamp >= recencyThreshold,
            cancellationToken);
    }
}
