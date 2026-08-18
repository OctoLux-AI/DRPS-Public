using System.Diagnostics;

namespace Drps.Diagnostics;

// Exists specifically because .NET's HttpClient was confirmed, empirically, during this
// task's own testing to be unreliable against fred.stlouisfed.org - across roughly a dozen
// live trials, HttpClient hung to its own configured timeout or had its connection forcibly
// reset by the remote host in the clear majority of attempts (regardless of HTTP/1.1-forced
// vs. default HTTP/2 negotiation, regardless of timeout length up to 300s), while curl.exe
// against the IDENTICAL URLs succeeded in 1-2 seconds every single time it was tried. This is
// not a timeout-tuning problem - CLAUDE.md's own "Regime Data Sourcing" block already flagged
// FRED as slow/flaky in a general sense, but this task found it specifically implicates .NET's
// HTTP stack against this host, not FRED itself (curl proves the server responds quickly and
// reliably). Shelling out to curl.exe - already a standard, Windows-bundled tool this same
// investigation used as its own empirical cross-check - is the pragmatic fix: it reuses the
// exact client that has actually been proven to work, rather than continuing to chase a
// HttpClient-specific negotiation quirk with no confirmed root cause.
public static class CurlHttpFetcher
{
    public static async Task<string> FetchStringAsync(string url, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "curl.exe",
            ArgumentList = { "-s", "-S", "-m", timeoutSeconds.ToString(), url },
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

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"curl.exe exited {process.ExitCode} fetching {url}: {(string.IsNullOrWhiteSpace(stderr) ? "(no stderr output)" : stderr.Trim())}");
        }

        return stdout;
    }
}
