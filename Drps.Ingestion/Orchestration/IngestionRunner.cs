using Drps.Ingestion.Feeders;
using Drps.Ingestion.Persistence;
using Drps.Shared.Exceptions;

namespace Drps.Ingestion.Orchestration;

public class IngestionRunner
{
    private readonly IEnumerable<IMarketDataFeeder> _feeders;
    private readonly DrpsDbContext _dbContext;
    private readonly SourceStatusTracker _sourceStatusTracker;
    private readonly ILogger<IngestionRunner> _logger;

    public IngestionRunner(
        IEnumerable<IMarketDataFeeder> feeders,
        DrpsDbContext dbContext,
        SourceStatusTracker sourceStatusTracker,
        ILogger<IngestionRunner> logger)
    {
        _feeders = feeders;
        _dbContext = dbContext;
        _sourceStatusTracker = sourceStatusTracker;
        _logger = logger;
    }

    public async Task RunAsync(string symbol, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        var succeededCount = 0;
        var failedCount = 0;
        var configErrorCount = 0;
        var notFoundCount = 0;
        var noDataCount = 0;

        foreach (var feeder in _feeders)
        {
            // IMarketDataFeeder implementations are documented to catch their own exceptions
            // and surface failure via Success = false, but this loop can't assume that
            // guarantee holds for every future/third-party feeder — one feeder's failure,
            // thrown or reported, must never stop the others from running.
            try
            {
                var result = await feeder.FetchDailyBarsAsync(symbol, start, end, cancellationToken);

                if (result.Success)
                {
                    // Raw append only — never update or check for existing rows. Duplicates
                    // across repeated runs are expected here; deduplication is a later pass.
                    _dbContext.RawOhlcvBars.AddRange(result.Bars);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }

                if (result.ExDividendObservations.Count > 0)
                {
                    // Independent write path from the OHLCV bars above — separate table,
                    // separate entity type, separate SaveChangesAsync call. Per CLAUDE.md's
                    // "Ex-Dividend Source: Tiingo Replaces Finnhub" decision (2026-08-01):
                    // TiingoFeeder is the only feeder that ever populates this list (empty for
                    // every other IMarketDataFeeder), and this write deliberately does NOT
                    // route through SourceStatusTracker for the ExDividend field/bar type —
                    // this data rides the OHLCV fetch's own success/failure, it is not an
                    // independently-attempted fetch with its own trust state to track. Same
                    // append-only, raw-value discipline as the bars write above.
                    _dbContext.RawExDividendObservations.AddRange(result.ExDividendObservations);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }

                if (result.Success && result.NoDataForRange)
                {
                    // A well-formed response with zero bars - ambiguous by design (bad
                    // ticker vs. a legitimate empty range such as a holiday or pre-IPO
                    // gap; the source's response is identical either way). Deliberately
                    // does NOT call SourceStatusTracker.RecordResultAsync: this is neither
                    // a real success (must not count toward n=5 Trusted-promotion, since
                    // it confirms nothing about cross-source agreement) nor a real failure
                    // (must not strike ConsecutiveFailures, since the source behaved
                    // correctly).
                    noDataCount++;
                    _logger.LogWarning(
                        "[INGESTION-RUNNER]: {Source} returned zero bars for {Symbol} {Start}-{End} " +
                        "(ambiguous: invalid/delisted ticker or a legitimate empty range) - not counted toward trust state",
                        feeder.Source, symbol, start, end);
                    continue;
                }

                await _sourceStatusTracker.RecordResultAsync(
                    feeder.Source, SourceStatusTracker.DailyBarFieldOrBarType, result.Success, cancellationToken);

                if (result.Success)
                {
                    succeededCount++;
                }
                else
                {
                    failedCount++;
                    _logger.LogWarning(
                        "[INGESTION-RUNNER]: {Source} failed for {Symbol}: {ErrorMessage}",
                        feeder.Source, symbol, result.ErrorMessage);
                }
            }
            catch (ConfigurationMissingException ex)
            {
                // A missing credential means no HTTP call was ever attempted - this is a
                // client-side configuration bug, not the source rejecting a real request.
                // Deliberately does NOT call SourceStatusTracker.RecordResultAsync: this must
                // never count toward the 3-strikes-dead threshold, unlike a genuine failure.
                configErrorCount++;
                _logger.LogError(
                    "[INGESTION-RUNNER]: {Source} skipped for {Symbol} - configuration error, not a source failure: {Message}",
                    feeder.Source, symbol, ex.Message);
            }
            catch (SymbolNotFoundException ex)
            {
                // The source explicitly confirmed no data exists for this symbol (e.g. a
                // 404 for an unknown/delisted ticker) - a real response was received and
                // correctly interpreted, so this is bad INPUT, not source unreliability.
                // Deliberately does NOT call SourceStatusTracker.RecordResultAsync: neither a
                // success nor a real failure, so ConsecutiveFailures must not move in either
                // direction.
                notFoundCount++;
                _logger.LogWarning(
                    "[INGESTION-RUNNER]: {Source} reported no data for {Symbol} - symbol not found, not a source failure: {Message}",
                    feeder.Source, symbol, ex.Message);
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(
                    ex, "[INGESTION-RUNNER]: unexpected exception processing {Source} for {Symbol}",
                    feeder.Source, symbol);
            }
        }

        _logger.LogInformation(
            "[INGESTION-RUNNER]: {Symbol} {Start}-{End}: {SucceededCount} feeder(s) succeeded, {FailedCount} failed, {ConfigErrorCount} skipped (configuration error), {NotFoundCount} skipped (symbol not found), {NoDataCount} skipped (zero bars, ambiguous)",
            symbol, start, end, succeededCount, failedCount, configErrorCount, notFoundCount, noDataCount);
    }
}
