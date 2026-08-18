using System.Text.Json;
using Drps.Shared.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Drps.Ingestion.Feeders;

/// <summary>
/// Live, single-call sentiment lookup against Marketaux's /news/all endpoint (Sentiment
/// Adjuster Multiplier Decision, CLAUDE.md 2026-07-24). Real response shape confirmed
/// empirically before this class was written (Marketaux diagnostic, 2026-07-24) -
/// {"meta":{"found":N,"returned":N}, "data":[{"entities":[{"symbol":"...",
/// "sentiment_score": 0.12 | null}, ...]}, ...]}. Reads entities[].symbol/sentiment_score
/// only - never highlights[].sentiment, per this task's explicit instruction; the two fields
/// are not interchangeable (entity-level score can be null while a highlight-level score
/// exists underneath, and vice versa) and mixing them would silently change what the
/// multiplier is actually measuring.
///
/// Deliberately no persistence (unlike every DrpsDbContext-backed feeder in this project) and
/// deliberately no Polly retry policy - this is a fire-time read for a bidirectional sizing
/// adjustment, not a source DRPS accumulates history from. A single non-2xx response, timeout,
/// or malformed body is logged and returned as <see cref="MarketauxSentimentOutcome.CallFailed"/>
/// immediately; no retry, no loop, matching this task's own explicit spec.
///
/// Not wired into DI/Program.cs yet - deliberately standalone per the Sentiment Adjuster
/// Multiplier task's own scope (that wiring, plus registering the named HttpClient this class
/// resolves via <see cref="HttpClientName"/>, is a separate, later task).
/// </summary>
public class MarketauxSentimentClient
{
    public const string HttpClientName = "MarketauxSentimentClient";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MarketauxOptions _options;
    private readonly ILogger<MarketauxSentimentClient> _logger;

    public MarketauxSentimentClient(
        IHttpClientFactory httpClientFactory,
        IOptions<MarketauxOptions> options,
        ILogger<MarketauxSentimentClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<MarketauxSentimentResult> GetSentimentAsync(string ticker, CancellationToken cancellationToken)
    {
        // Checked before any HTTP call is attempted, same ConfigurationMissingException
        // precedent as every other feeder in this project (FinnhubEarningsFeeder,
        // TiingoFeeder, EdgarForm4Feeder) - a missing credential is a client-side
        // configuration bug, not a source failure.
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ConfigurationMissingException("Marketaux:ApiKey");

        // Marketaux's own confirmed contract (2026-07-24 diagnostic): the token is a query
        // string parameter (api_token), not a header, unlike Finnhub's X-Finnhub-Token. This
        // means the live key is baked directly into the request URL - never logged anywhere
        // below, including inside LogFailure's redaction, specifically because of that.
        var url = $"{_options.BaseUrl}/news/all?symbols={Uri.EscapeDataString(ticker)}" +
                  $"&api_token={Uri.EscapeDataString(_options.ApiKey)}";

        HttpResponseMessage response;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            response = await client.GetAsync(url, cancellationToken);
        }
        catch (Exception ex)
        {
            LogFailure(ticker, ex);
            return new MarketauxSentimentResult { Outcome = MarketauxSentimentOutcome.CallFailed, SentimentScore = null };
        }

        if (!response.IsSuccessStatusCode)
        {
            // Status code only - never the response body (which could echo the request URL
            // back, e.g. some APIs include the original query string in a 4xx error payload)
            // and never the request URL itself.
            _logger.LogWarning(
                "[MARKETAUX-SENTIMENT-CLIENT]: non-success status {StatusCode} for {Ticker}",
                (int)response.StatusCode, ticker);
            return new MarketauxSentimentResult { Outcome = MarketauxSentimentOutcome.CallFailed, SentimentScore = null };
        }

        try
        {
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseResponse(json, ticker);
        }
        catch (Exception ex)
        {
            LogFailure(ticker, ex);
            return new MarketauxSentimentResult { Outcome = MarketauxSentimentOutcome.CallFailed, SentimentScore = null };
        }
    }

    // CRITICAL (Sentiment Adjuster Multiplier task): the API key must never reach a log line,
    // console, or exception-formatted output under any circumstance. Deliberately never passes
    // `ex` itself to the logger (ILogger's default formatter prints ex.ToString(), which walks
    // every InnerException's Message too - a redaction of just the top-level Message would not
    // protect against that path). Only a redacted, flattened string is ever logged.
    private void LogFailure(string ticker, Exception ex)
    {
        var redactedMessage = Redact(FlattenMessages(ex), _options.ApiKey);
        _logger.LogWarning(
            "[MARKETAUX-SENTIMENT-CLIENT]: fetch failed for {Ticker} ({ExceptionType}): {Message}",
            ticker, ex.GetType().Name, redactedMessage);
    }

    private static string FlattenMessages(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }
        return string.Join(" -> ", messages);
    }

    private static string Redact(string text, string apiKey) =>
        string.IsNullOrEmpty(apiKey) ? text : text.Replace(apiKey, "[REDACTED]", StringComparison.Ordinal);

    // Public, not private - testable directly against a raw response body without needing an
    // HTTP round-trip, same "public static helper" precedent as
    // EdgarForm4Feeder.ParseForm4FilingsInWindow. Searches every article's entities array in
    // response order; the first entity matching `ticker` with a non-null score wins. If one or
    // more entities match the ticker but every match has a null score, that's
    // NullSentimentScore; if nothing anywhere matches the ticker at all (including a genuinely
    // empty data array, the confirmed shape for a malformed/uncovered symbol), that's
    // NoMatchingEntity. Throws on a genuinely unexpected top-level shape (missing/non-array
    // "data") - propagates to the caller, which treats it identically to a fetch-level failure,
    // same "unexpected shape is a fetch-level failure" precedent EdgarForm4Feeder's own doc
    // comment already establishes.
    public static MarketauxSentimentResult ParseResponse(string json, string ticker)
    {
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("data", out var dataElement)
            || dataElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Marketaux /news/all response was not in the expected data[] shape");
        }

        int? articlesFound = null;
        int? articlesReturned = null;
        if (doc.RootElement.TryGetProperty("meta", out var metaElement) && metaElement.ValueKind == JsonValueKind.Object)
        {
            if (metaElement.TryGetProperty("found", out var foundElement) && foundElement.ValueKind == JsonValueKind.Number)
                articlesFound = foundElement.GetInt32();

            if (metaElement.TryGetProperty("returned", out var returnedElement) && returnedElement.ValueKind == JsonValueKind.Number)
                articlesReturned = returnedElement.GetInt32();
        }

        var sawMatchingEntityWithNullScore = false;

        foreach (var article in dataElement.EnumerateArray())
        {
            if (article.ValueKind != JsonValueKind.Object
                || !article.TryGetProperty("entities", out var entitiesElement)
                || entitiesElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entity in entitiesElement.EnumerateArray())
            {
                if (entity.ValueKind != JsonValueKind.Object
                    || !entity.TryGetProperty("symbol", out var symbolElement)
                    || symbolElement.ValueKind != JsonValueKind.String
                    || symbolElement.GetString() != ticker)
                {
                    continue;
                }

                if (entity.TryGetProperty("sentiment_score", out var scoreElement)
                    && scoreElement.ValueKind == JsonValueKind.Number)
                {
                    return new MarketauxSentimentResult
                    {
                        Outcome = MarketauxSentimentOutcome.RealValue,
                        SentimentScore = scoreElement.GetDecimal(),
                        ArticlesFound = articlesFound,
                        ArticlesReturned = articlesReturned
                    };
                }

                // Matching symbol, but sentiment_score is null or absent - keep scanning in
                // case a later article has a real score for the same ticker, but remember this
                // so a real match is never misreported as "no matching entity at all."
                sawMatchingEntityWithNullScore = true;
            }
        }

        return new MarketauxSentimentResult
        {
            Outcome = sawMatchingEntityWithNullScore
                ? MarketauxSentimentOutcome.NullSentimentScore
                : MarketauxSentimentOutcome.NoMatchingEntity,
            SentimentScore = null,
            ArticlesFound = articlesFound,
            ArticlesReturned = articlesReturned
        };
    }
}
