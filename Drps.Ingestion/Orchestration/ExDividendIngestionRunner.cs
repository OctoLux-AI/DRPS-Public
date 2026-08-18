using Drps.Ingestion.Feeders;
using Drps.Ingestion.Persistence;
using Drps.Shared.Exceptions;

namespace Drps.Ingestion.Orchestration;

// Parallel to IngestionRunner rather than folded into it - IMarketDataFeeder/IngestionRunner
// are bar-shaped (FeedFetchResult.Bars: IReadOnlyList<RawOhlcvBar>), and ex-dividend
// observations don't fit that shape. Same pattern reused deliberately (ConfigurationMissingException
// carve-out, SourceStatusTracker recording, per-feeder isolation, NoDataForRange ambiguity
// handling) rather than the same class, since the underlying entity type differs.
public class ExDividendIngestionRunner
{
    private readonly IEnumerable<IExDividendFeeder> _feeders;
    private readonly DrpsDbContext _dbContext;
    private readonly SourceStatusTracker _sourceStatusTracker;
    private readonly ILogger<ExDividendIngestionRunner> _logger;

    public ExDividendIngestionRunner(
        IEnumerable<IExDividendFeeder> feeders,
        DrpsDbContext dbContext,
        SourceStatusTracker sourceStatusTracker,
        ILogger<ExDividendIngestionRunner> logger)
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
        var noDataCount = 0;
        var deadSkippedCount = 0;

        foreach (var feeder in _feeders)
        {
            // 3-strikes-dead rule (CLAUDE.md's "Dead source rule") - checked before every
            // attempt, not just recorded after one. Without this, RecordResultAsync marking a
            // source Dead has no actual effect: ConsecutiveFailures keeps climbing well past
            // the 3-strike threshold and the feeder keeps getting called indefinitely, which is
            // the exact bug this check closes. Deliberately does NOT call
            // SourceStatusTracker.RecordResultAsync for a skip - no fetch was attempted, so
            // there is no real outcome to record, same "never attempted" carve-out already
            // applied to ConfigurationMissingException below.
            if (await _sourceStatusTracker.IsDeadAsync(feeder.Source, SourceStatusTracker.ExDividendFieldOrBarType, cancellationToken))
            {
                deadSkippedCount++;
                _logger.LogWarning(
                    "[EXDIV-INGESTION-RUNNER]: {Source} is marked Dead for ExDividend - skipping fetch for {Symbol}, per the 3-strikes-dead rule",
                    feeder.Source, symbol);
                continue;
            }

            // Same defensive isolation as IngestionRunner: one feeder's failure, thrown or
            // reported, must never stop the others from running.
            try
            {
                var result = await feeder.FetchExDividendsAsync(symbol, start, end, cancellationToken);

                if (result.Success)
                {
                    // Raw append only - never update or check for existing rows. Duplicates
                    // across repeated runs are expected here; deduplication is a later pass.
                    _dbContext.RawExDividendObservations.AddRange(result.Observations);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }

                if (result.Success && result.NoDataForRange)
                {
                    // A well-formed response with zero ex-dividend events - ambiguous by
                    // design (invalid ticker vs. a legitimate non-dividend-paying stock; the
                    // source's response is identical either way). Deliberately does NOT call
                    // SourceStatusTracker.RecordResultAsync: neither a real success (must not
                    // count toward n=5 Trusted-promotion) nor a real failure (must not strike
                    // ConsecutiveFailures, since the source behaved correctly).
                    noDataCount++;
                    _logger.LogWarning(
                        "[EXDIV-INGESTION-RUNNER]: {Source} returned zero ex-dividend events for {Symbol} {Start}-{End} " +
                        "(ambiguous: invalid ticker or a legitimate non-dividend-paying stock) - not counted toward trust state",
                        feeder.Source, symbol, start, end);
                    continue;
                }

                await _sourceStatusTracker.RecordResultAsync(
                    feeder.Source, SourceStatusTracker.ExDividendFieldOrBarType, result.Success, cancellationToken);

                if (result.Success)
                {
                    succeededCount++;
                }
                else
                {
                    failedCount++;
                    _logger.LogWarning(
                        "[EXDIV-INGESTION-RUNNER]: {Source} failed for {Symbol}: {ErrorMessage}",
                        feeder.Source, symbol, result.ErrorMessage);
                }
            }
            catch (ConfigurationMissingException ex)
            {
                // A missing credential means no HTTP call was ever attempted - a client-side
                // configuration bug, not the source rejecting a real request. Deliberately
                // does NOT call SourceStatusTracker.RecordResultAsync: must never count toward
                // the 3-strikes-dead threshold, unlike a genuine failure.
                configErrorCount++;
                _logger.LogError(
                    "[EXDIV-INGESTION-RUNNER]: {Source} skipped for {Symbol} - configuration error, not a source failure: {Message}",
                    feeder.Source, symbol, ex.Message);
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(
                    ex, "[EXDIV-INGESTION-RUNNER]: unexpected exception processing {Source} for {Symbol}",
                    feeder.Source, symbol);
            }
        }

        _logger.LogInformation(
            "[EXDIV-INGESTION-RUNNER]: {Symbol} {Start}-{End}: {SucceededCount} feeder(s) succeeded, {FailedCount} failed, {ConfigErrorCount} skipped (configuration error), {NoDataCount} skipped (zero events, ambiguous), {DeadSkippedCount} skipped (source marked Dead)",
            symbol, start, end, succeededCount, failedCount, configErrorCount, noDataCount, deadSkippedCount);
    }
}
