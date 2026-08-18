namespace Drps.Ingestion.Feeders;

/// <summary>
/// Simple proactive spacing between consecutive SEC EDGAR requests. Unlike Polly's
/// exponential-backoff retry policy elsewhere in this codebase (reactive - triggered by a
/// 429/5xx response), SEC has no API key and no published hard per-key rate limit at all;
/// its fair-use policy instead expects requesters to self-throttle before ever being told
/// to slow down. Registered as a singleton so the "last request" timestamp is shared across
/// every DI scope in the process - same reasoning as SecEdgarCikResolver's cache needing to
/// be process-wide rather than rebuilt per scope (SectorWorker creates a fresh scope per
/// ticker in its watchlist loop).
/// </summary>
public class SecEdgarRateLimiter
{
    // Reasoned placeholder, not a measured/documented SEC limit (none is published) - same
    // "needs real-world calibration" category as other numeric placeholders in this
    // codebase. 200ms keeps this comfortably under 10 requests/second even under
    // concurrent callers.
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(200);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRequestAt = DateTime.MinValue;

    public async Task WaitForSlotAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var remaining = MinimumInterval - (DateTime.UtcNow - _lastRequestAt);
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, cancellationToken);

            _lastRequestAt = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}
