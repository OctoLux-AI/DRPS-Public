using Drps.Ingestion.Feeders;
using Drps.Ingestion.Persistence;
using Drps.Shared.Exceptions;

namespace Drps.Ingestion.Orchestration;

// Parallel to ExDividendIngestionRunner rather than folded into it - sector observations
// are keyed by SectorSourceType, a separate domain from SourceType's dead-source-tracking
// enum, and RawSectorObservation.Verified is asserted directly by the feeder (Finnhub is
// primary, n=1 by design) rather than computed by a reconciliation service. Deliberately
// does NOT call SourceStatusTracker - that tracker is scoped to SourceType (the
// OHLCV/ex-dividend domain's dead-source 3-strikes rule), and sector data is informational
// only, never cross-verified, per RawSectorObservation's own doc comment.
public class SectorIngestionRunner
{
    private readonly IEnumerable<ISectorFeeder> _feeders;
    private readonly DrpsDbContext _dbContext;
    private readonly ILogger<SectorIngestionRunner> _logger;

    public SectorIngestionRunner(IEnumerable<ISectorFeeder> feeders, DrpsDbContext dbContext, ILogger<SectorIngestionRunner> logger)
    {
        _feeders = feeders;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task RunAsync(string ticker, CancellationToken cancellationToken)
    {
        var succeededCount = 0;
        var failedCount = 0;
        var configErrorCount = 0;
        var noDataCount = 0;

        foreach (var feeder in _feeders)
        {
            // Same defensive isolation as ExDividendIngestionRunner: one feeder's failure,
            // thrown or reported, must never stop the others from running.
            try
            {
                var result = await feeder.FetchSectorAsync(ticker, cancellationToken);

                if (result.Success && result.NoDataForRange)
                {
                    // A well-formed response with no usable classification - ambiguous by
                    // design (unrecognized ticker vs. a real company Finnhub simply has no
                    // industry data for). Never persisted, never logged as a real failure.
                    noDataCount++;
                    _logger.LogWarning(
                        "[SECTOR-INGESTION-RUNNER]: {Source} returned no sector classification for {Ticker} " +
                        "(ambiguous: unrecognized ticker or a company Finnhub has no industry data for) - not persisted",
                        feeder.Source, ticker);
                    continue;
                }

                if (result.Success)
                {
                    // Raw append only - never update or check for existing rows, same
                    // convention as every other raw observation table.
                    _dbContext.RawSectorObservations.AddRange(result.Observations);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    succeededCount++;
                }
                else
                {
                    failedCount++;
                    _logger.LogWarning(
                        "[SECTOR-INGESTION-RUNNER]: {Source} failed for {Ticker}: {ErrorMessage}",
                        feeder.Source, ticker, result.ErrorMessage);
                }
            }
            catch (ConfigurationMissingException ex)
            {
                // A missing credential means no HTTP call was ever attempted - a client-side
                // configuration bug, not a source failure. Never persisted.
                configErrorCount++;
                _logger.LogError(
                    "[SECTOR-INGESTION-RUNNER]: {Source} skipped for {Ticker} - configuration error, not a source failure: {Message}",
                    feeder.Source, ticker, ex.Message);
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(
                    ex, "[SECTOR-INGESTION-RUNNER]: unexpected exception processing {Source} for {Ticker}",
                    feeder.Source, ticker);
            }
        }

        _logger.LogInformation(
            "[SECTOR-INGESTION-RUNNER]: {Ticker}: {SucceededCount} feeder(s) succeeded, {FailedCount} failed, {ConfigErrorCount} skipped (configuration error), {NoDataCount} skipped (no classification, ambiguous)",
            ticker, succeededCount, failedCount, configErrorCount, noDataCount);
    }
}
