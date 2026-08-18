namespace Drps.Shared.BuildCurrency;

/// <summary>
/// Raw result of a single git invocation via IGitCommandRunner - exit code plus both output
/// streams, deliberately not yet interpreted (that's BuildCurrencyChecker's job). Kept as its
/// own small record so IGitCommandRunner has no dependency on BuildCurrencyChecker's own
/// result shape - a fake in a test can construct this directly with no other type involved.
/// </summary>
public sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError);
