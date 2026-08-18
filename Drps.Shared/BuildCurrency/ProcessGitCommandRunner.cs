using System.Diagnostics;

namespace Drps.Shared.BuildCurrency;

/// <summary>
/// Real IGitCommandRunner - shells out to git.exe, same ProcessStartInfo shape already
/// established by Drps.Ingestion's CurlFredCsvTransport (CLAUDE.md, 2026-07-28) for invoking an
/// external CLI tool from inside a running Worker process. Never throws on a non-zero exit -
/// that's a normal, expected outcome here (e.g. "bad revision" if the embedded commit hash
/// isn't reachable in this repo's history) and is surfaced via GitCommandResult.ExitCode for
/// BuildCurrencyChecker to interpret, not as an exception. A genuine failure to even start the
/// process (git.exe not on PATH, working directory doesn't exist) still throws naturally from
/// Process.Start()/WaitForExitAsync() - BuildCurrencyChecker's own try/catch around this call is
/// what turns that into a fail-open "could not verify" result, not this class.
/// </summary>
public sealed class ProcessGitCommandRunner : IGitCommandRunner
{
    public async Task<GitCommandResult> RunAsync(string workingDirectory, string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new GitCommandResult(process.ExitCode, stdout, stderr);
    }
}
