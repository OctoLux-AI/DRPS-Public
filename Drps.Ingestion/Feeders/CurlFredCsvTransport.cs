using System.Diagnostics;

namespace Drps.Ingestion.Feeders;

/// <summary>
/// Production fix for a real, empirically-confirmed reliability defect: .NET's HttpClient is
/// unreliable against fred.stlouisfed.org in this environment, while curl.exe against the
/// identical URLs is not. First found by Drps.Diagnostics' regime-distribution tool
/// (CLAUDE.md, 2026-07-28) - roughly a dozen live HttpClient trials there hung or had their
/// connection forcibly reset in the clear majority of attempts, regardless of timeout length
/// or HTTP version forced, while curl.exe succeeded in 1-2 seconds every single time. This
/// task reproduced the same pattern directly against FredRegimeFeeder's own real, unmodified
/// production code path (real IHttpClientFactory-resolved HttpClient, real 100-second timeout,
/// real FeederRetryPolicy Polly retry): 4 of 8 live trials failed (50%), each burning ~414
/// seconds exhausting all 3 Polly retries before finally giving up - the exact shape of the
/// real 2026-07-27 VIX3M/FRED transport-failure incident the partial-success guard fix
/// (CLAUDE.md, same date) was built to catch, not a one-off.
///
/// Shells out to curl.exe (already the standard, Windows-bundled tool used to empirically
/// diagnose this same host across two separate investigations) rather than continuing to fight
/// HttpClient's negotiation against this specific host with no confirmed root cause. Cboe
/// direct (CboeRegimeFeeder) showed no such issue in either investigation and is unchanged -
/// this fix is scoped to FRED specifically, not applied to every HTTP-backed feeder in this
/// codebase.
/// </summary>
public sealed class CurlFredCsvTransport : IFredCsvTransport
{
    private readonly int _timeoutSeconds;

    // 100s matches FredRegimeClient's own prior HttpClient.Timeout registration - curl's -m
    // flag is the direct analogue, so this preserves the same per-attempt time budget rather
    // than picking a new number.
    public CurlFredCsvTransport(int timeoutSeconds = 100)
    {
        _timeoutSeconds = timeoutSeconds;
    }

    public async Task<string> FetchCsvAsync(string url, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "curl.exe",
            // -f (--fail): makes curl exit non-zero on an HTTP 4xx/5xx response instead of
            // exiting 0 with the error page's body as "successful" stdout - without this,
            // a genuine 5xx from FRED would silently fail this feeder's header check instead
            // of being retried the way IsTransient() used to retry a 5xx under the old
            // HttpClient-based path. Not present in Drps.Diagnostics' own CurlHttpFetcher,
            // which only ever fetches known-good static CSV endpoints in a one-shot analysis
            // context and has no retry loop to feed.
            ArgumentList = { "-s", "-S", "-f", "-m", _timeoutSeconds.ToString(), url },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        // A non-zero exit covers both a genuine curl-level failure and curl's own -m timeout
        // expiring (curl exits 28 on timeout) - both are the same "this attempt didn't work"
        // outcome from FredRegimeFeeder's point of view, and both are handled identically by
        // FeederRetryPolicy.BuildForCurlFailures's InvalidOperationException retry.
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"curl.exe exited {process.ExitCode} fetching {url}: {(string.IsNullOrWhiteSpace(stderr) ? "(no stderr output)" : stderr.Trim())}");
        }

        return stdout;
    }
}
