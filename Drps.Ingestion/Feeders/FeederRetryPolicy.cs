using System.Net;
using Polly;
using Polly.Retry;

namespace Drps.Ingestion.Feeders;

// Shared retry-policy shape for AlpacaFeeder and TiingoFeeder - previously duplicated
// identically in each, which is exactly how a timeout-handling gap ended up present in
// both at once. Centralizing here means a future fix only has to happen in one place.
// Public (not internal) so Drps.Execution's AlpacaTradingClient can reuse it for its own
// read-only GET calls rather than re-duplicating the exact gap this class exists to close -
// see AlpacaTradingClient's own doc comment for why PlaceOrderAsync specifically does not use it.
public static class FeederRetryPolicy
{
    private const int MaxRetries = 3;

    // Retries happen inside a single fetch attempt: the policy exhausts MaxRetries before
    // control returns to the feeder's caller. Per CLAUDE.md's dead-source rule, a
    // "strike" is only counted once every retry for one attempt is exhausted — that
    // counter is orchestration-layer logic, out of scope here.
    public static AsyncRetryPolicy<HttpResponseMessage> Build(string feederName, ILogger logger)
    {
        return Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            // A genuine network-level timeout on HttpClient.GetAsync throws
            // TaskCanceledException wrapping a TimeoutException (confirmed .NET 5+
            // behavior, still true on net10.0) - retried the same as a 5xx/429, since
            // it's exactly the kind of transient blip retries exist to smooth over.
            // Deliberately excludes a caller-triggered cancellation (host shutdown, the
            // CancellationToken passed into FetchDailyBarsAsync firing), which surfaces
            // as TaskCanceledException without that inner TimeoutException - that must
            // propagate immediately, not be retried, so shutdown isn't delayed by a
            // pointless backoff.
            .Or<TaskCanceledException>(ex => ex.InnerException is TimeoutException)
            .OrResult(response => IsTransient(response.StatusCode))
            .WaitAndRetryAsync(
                retryCount: MaxRetries,
                sleepDurationProvider: (attempt, outcome, _) =>
                    outcome.Result?.Headers.RetryAfter?.Delta
                        ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetryAsync: (outcome, delay, attempt, _) =>
                {
                    logger.LogWarning(
                        "[{FeederName}-FEEDER/RETRY]: attempt {Attempt}/{MaxRetries} failed ({Reason}), retrying in {Delay}s",
                        feederName, attempt, MaxRetries,
                        outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString(),
                        delay.TotalSeconds);
                    return Task.CompletedTask;
                });
    }

    // 429 (rate limit) and 408 (timeout) are transient by nature; 5xx is the server's own
    // failure. Anything else (401/403/404/etc.) is a genuine error retrying won't fix.
    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests
        || statusCode == HttpStatusCode.RequestTimeout
        || (int)statusCode >= 500;

    // Non-generic counterpart to Build() above, for transports that don't return an
    // HttpResponseMessage to inspect - specifically CurlFredCsvTransport (FredRegimeFeeder,
    // CLAUDE.md's "Production FredRegimeFeeder curl.exe Fix" decision, 2026-07-28). A curl.exe
    // invocation either returns the CSV body or throws InvalidOperationException (non-zero
    // exit code, including curl's own -m timeout expiring) - there's no status-code-shaped
    // result to apply OrResult against, so this only needs the exception-handling half of the
    // shape above. Same MaxRetries/exponential-backoff discipline, same onRetry logging
    // convention, so a curl-backed feeder's retry behavior reads identically to every
    // HttpClient-backed feeder's in the logs.
    public static AsyncRetryPolicy BuildForCurlFailures(string feederName, ILogger logger)
    {
        return Policy
            .Handle<InvalidOperationException>()
            .WaitAndRetryAsync(
                retryCount: MaxRetries,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetryAsync: (ex, delay, attempt, _) =>
                {
                    logger.LogWarning(
                        "[{FeederName}-FEEDER/RETRY]: attempt {Attempt}/{MaxRetries} failed ({Reason}), retrying in {Delay}s",
                        feederName, attempt, MaxRetries, ex.Message, delay.TotalSeconds);
                    return Task.CompletedTask;
                });
    }
}
