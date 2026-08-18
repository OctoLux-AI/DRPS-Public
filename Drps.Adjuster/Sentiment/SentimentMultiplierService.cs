using Drps.Adjuster.Configuration;
using Drps.Ingestion.Feeders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Drps.Adjuster.Sentiment;

/// <summary>
/// Fire-time, bidirectional sizing-adjustment multiplier from Marketaux news sentiment
/// (Sentiment Adjuster Multiplier Decision, CLAUDE.md 2026-07-24). Structurally new for this
/// codebase - unlike the existing Form 4 multiplier (InsiderLookupService: upgrade-only,
/// computed at scan time, reads persisted RawInsiderObservation rows), this multiplier can move
/// the allocation both up and down, and is computed live via a single
/// <see cref="MarketauxSentimentClient"/> call rather than any persisted history.
///
/// multiplier = 1.0 + (sentiment_score x ScalingFactor), clamped to [MultiplierFloor,
/// MultiplierCeiling]. Fail-closed default: any outcome other than a real, non-null score
/// resolves to 1.0 (neutral) - a missing/failed read must never bias sizing in either
/// direction. Every non-real-value case is still logged with which specific case triggered the
/// neutral fallback (no-matching-entity vs null-score vs call-failure), per this task's own
/// instruction, even though the numeric outcome (1.0) is identical for all three - these are
/// different failure classes worth being able to distinguish later.
///
/// Standalone - not called by AdjusterSizingService/AdjusterScanService, PreFireGateService, or
/// OrderFiringService yet, and not registered in Program.cs. That wiring (including how this
/// multiplier stacks with the existing insider multiplier, and whether it needs its own
/// independent cap the way the Insider Form 4 Adjuster Multiplier Decision required) is a
/// separate, later task - deliberately not decided or implemented here.
/// </summary>
public class SentimentMultiplierService
{
    private readonly MarketauxSentimentClient _client;
    private readonly SentimentMultiplierOptions _options;
    private readonly ILogger<SentimentMultiplierService> _logger;

    public SentimentMultiplierService(
        MarketauxSentimentClient client,
        IOptions<SentimentMultiplierOptions> options,
        ILogger<SentimentMultiplierService> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<decimal> GetMultiplierAsync(string ticker, CancellationToken cancellationToken)
    {
        var result = await _client.GetSentimentAsync(ticker, cancellationToken);

        if (result.Outcome == MarketauxSentimentOutcome.RealValue)
        {
            // Outcome.RealValue is the only case where SentimentScore is guaranteed non-null
            // (MarketauxSentimentClient.ParseResponse's own contract) - the null-forgiving
            // access here is safe specifically because of that invariant, not an assumption
            // made independently by this class.
            var multiplier = ComputeMultiplier(result.SentimentScore!.Value, _options);
            _logger.LogInformation(
                "[SENTIMENT-MULTIPLIER]: {Ticker} real sentiment_score={SentimentScore}, multiplier={Multiplier}",
                ticker, result.SentimentScore, multiplier);
            return multiplier;
        }

        // Every other outcome is fail-closed neutral (1.0) - the multiplier is bidirectional,
        // so unlike the insider multiplier's upgrade-only floor of 1.0, a missing/failed read
        // here must land exactly on the true neutral point, not a safe-looking floor.
        switch (result.Outcome)
        {
            case MarketauxSentimentOutcome.NoMatchingEntity:
                _logger.LogInformation(
                    "[SENTIMENT-MULTIPLIER]: {Ticker} no matching entity in Marketaux response ({ArticlesFound} articles found overall) - neutral multiplier",
                    ticker, result.ArticlesFound);
                break;

            case MarketauxSentimentOutcome.NullSentimentScore:
                _logger.LogInformation(
                    "[SENTIMENT-MULTIPLIER]: {Ticker} matching entity found but sentiment_score was null - neutral multiplier",
                    ticker);
                break;

            case MarketauxSentimentOutcome.CallFailed:
                _logger.LogWarning(
                    "[SENTIMENT-MULTIPLIER]: {Ticker} Marketaux call failed - neutral multiplier", ticker);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(result.Outcome), result.Outcome, "Unhandled MarketauxSentimentOutcome");
        }

        return 1.0m;
    }

    // [REDACTED FOR PUBLIC RELEASE]
    // Maps a raw sentiment score to a bidirectional sizing multiplier. The exact scaling/clamp
    // formula is proprietary and not included in this public repository - see README.md's
    // "What's intentionally not public" section.
    public static decimal ComputeMultiplier(decimal sentimentScore, SentimentMultiplierOptions options)
    {
        throw new NotImplementedException("Redacted for public release - see README.md");
    }
}
