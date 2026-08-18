namespace Drps.Shared.BuildCurrency;

/// <summary>
/// Startup self-check closing the exact gap the 2026-08-03 stale-publish incident (Ingestion
/// ran a build one merge behind the earnings-feeder reactivation, silently, with zero errors
/// logged anywhere) surfaced for a third time. CLAUDE.md's "Build-Currency Self-Check" design
/// decision (2026-08-03): each published assembly already carries the exact git commit it was
/// built from, for free, via the .NET SDK's own automatic embedding of the source revision into
/// AssemblyInformationalVersion (confirmed empirically during that design audit - no SourceLink
/// package, no MSBuild property, ever configured for this). This class reads that embedded
/// commit and asks one question: has anything relevant to this specific published binary
/// changed on main since it was built?
///
/// Deliberately path-scoped, not a bare "does the embedded hash equal main's HEAD" check -
/// comparing whole-repo HEAD would false-flag every project as stale the moment an unrelated
/// commit lands anywhere else in the solution (e.g. a Drps.Execution-only change, or a
/// CLAUDE.md-only doc commit) - noisy enough, fast enough, to get the alert ignored, which
/// defeats the entire point. The caller supplies exactly the paths relevant to its own
/// published output (its own project directory plus every ProjectReference that flows into
/// that output - see each Program.cs's own call site for the concrete list per project).
///
/// Compares against local "main" specifically, not "origin/main" - deliberately no network
/// dependency. This catches "merged and pulled locally, but never republished" (the exact
/// 2026-08-03 failure), not "merged on GitHub but never pulled locally at all" - a different,
/// so-far-unobserved failure mode, out of scope for this check.
///
/// Fail-open by design: any failure of the check itself (git not on PATH, working directory not
/// inside a git repo, a "bad revision" if the embedded hash isn't reachable in this repo's
/// history) resolves to BuildCurrencyStatus.Unverifiable, never an exception - the caller logs a
/// quiet warning and the host starts normally regardless. This must never block or delay
/// startup, matching the crash-survivability catch-log-continue pattern already used elsewhere
/// in these workers (e.g. IngestionJob's per-run isolation).
/// </summary>
public sealed class BuildCurrencyChecker
{
    private readonly IGitCommandRunner _gitRunner;

    public BuildCurrencyChecker(IGitCommandRunner gitRunner)
    {
        _gitRunner = gitRunner;
    }

    /// <summary>
    /// Extracts the git commit hash the .NET SDK appended to AssemblyInformationalVersion at
    /// build time (shape confirmed empirically: "1.0.0+{40-char hash}"). Returns null for a
    /// missing/blank version string or one with no "+" separator (e.g. a build produced outside
    /// a git working tree, where the SDK has nothing to embed) - callers treat null the same as
    /// any other unverifiable case, not as an error in this method.
    /// </summary>
    public static string? ExtractCommitHash(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return null;
        }

        var plusIndex = informationalVersion.IndexOf('+');
        if (plusIndex < 0 || plusIndex == informationalVersion.Length - 1)
        {
            return null;
        }

        return informationalVersion[(plusIndex + 1)..];
    }

    public async Task<BuildCurrencyCheckResult> CheckAsync(
        string? embeddedCommitHash,
        string workingDirectory,
        IReadOnlyList<string> relevantPaths,
        string mainRef = "main",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(embeddedCommitHash))
        {
            return BuildCurrencyCheckResult.Unverifiable(
                "no git commit hash found in this assembly's AssemblyInformationalVersion");
        }

        try
        {
            var logArguments = new List<string> { "log", "--oneline", $"{embeddedCommitHash}..{mainRef}", "--" };
            logArguments.AddRange(relevantPaths);
            var logResult = await _gitRunner.RunAsync(workingDirectory, logArguments.ToArray(), cancellationToken);

            if (logResult.ExitCode != 0)
            {
                return BuildCurrencyCheckResult.Unverifiable(
                    $"git log exited {logResult.ExitCode}: {FirstLineOrFallback(logResult.StandardError)}");
            }

            var commitLines = SplitNonEmptyLines(logResult.StandardOutput);
            if (commitLines.Length == 0)
            {
                return BuildCurrencyCheckResult.Current();
            }

            // Best-effort only - if the diff call itself fails for some reason, the commit
            // count from above is still real and worth reporting; an empty changed-paths list
            // is a strictly smaller loss than discarding the whole "this IS stale" finding.
            var diffArguments = new List<string> { "diff", "--name-only", embeddedCommitHash, mainRef, "--" };
            diffArguments.AddRange(relevantPaths);
            var diffResult = await _gitRunner.RunAsync(workingDirectory, diffArguments.ToArray(), cancellationToken);
            var changedPaths = diffResult.ExitCode == 0
                ? SplitNonEmptyLines(diffResult.StandardOutput)
                : Array.Empty<string>();

            return BuildCurrencyCheckResult.Stale(commitLines.Length, changedPaths);
        }
        catch (Exception ex)
        {
            return BuildCurrencyCheckResult.Unverifiable(ex.Message);
        }
    }

    private static string[] SplitNonEmptyLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

    private static string FirstLineOrFallback(string text)
    {
        var lines = SplitNonEmptyLines(text);
        return lines.Length > 0 ? lines[0] : "(no stderr output)";
    }
}
