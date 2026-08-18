namespace Drps.Shared.BuildCurrency;

/// <summary>
/// Seam over shelling out to git.exe, so BuildCurrencyChecker's actual decision logic (parsing
/// "is this build stale" out of a git log/diff result) is testable with a hand-rolled fake -
/// same no-mocking-library convention already used throughout this codebase (e.g.
/// IFredCsvTransport/FakeFredCsvTransport) - rather than requiring a real git repository and a
/// real git.exe in every test run.
/// </summary>
public interface IGitCommandRunner
{
    Task<GitCommandResult> RunAsync(string workingDirectory, string[] arguments, CancellationToken cancellationToken);
}
