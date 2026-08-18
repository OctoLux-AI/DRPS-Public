using Drps.Shared.BuildCurrency;
using Drps.Tests.TestHelpers;

namespace Drps.Tests;

public class BuildCurrencyCheckerTests
{
    private sealed class FakeGitCommandRunner : IGitCommandRunner
    {
        public GitCommandResult LogResult { get; set; } = new(0, string.Empty, string.Empty);
        public GitCommandResult DiffResult { get; set; } = new(0, string.Empty, string.Empty);
        public Exception? ThrowOnRun { get; set; }
        public List<string[]> Calls { get; } = new();

        public Task<GitCommandResult> RunAsync(string workingDirectory, string[] arguments, CancellationToken cancellationToken)
        {
            Calls.Add(arguments);

            if (ThrowOnRun is not null)
            {
                throw ThrowOnRun;
            }

            var isLogCall = arguments.Length > 0 && arguments[0] == "log";
            return Task.FromResult(isLogCall ? LogResult : DiffResult);
        }
    }

    // --- ExtractCommitHash ---

    [Fact]
    public void ExtractCommitHash_RealSdkEmbeddedFormat_ReturnsHashAfterPlus()
    {
        var hash = BuildCurrencyChecker.ExtractCommitHash("1.0.0+23761321f8a170e4bff6aad07ff5a9f10fade60f");

        Assert.Equal("23761321f8a170e4bff6aad07ff5a9f10fade60f", hash);
    }

    [Fact]
    public void ExtractCommitHash_NullInput_ReturnsNull()
    {
        Assert.Null(BuildCurrencyChecker.ExtractCommitHash(null));
    }

    [Fact]
    public void ExtractCommitHash_BlankInput_ReturnsNull()
    {
        Assert.Null(BuildCurrencyChecker.ExtractCommitHash("   "));
    }

    [Fact]
    public void ExtractCommitHash_NoPlusSeparator_ReturnsNull()
    {
        // A build produced outside a git working tree has nothing for the SDK to embed - the
        // version string is just "1.0.0" with no "+hash" suffix at all.
        Assert.Null(BuildCurrencyChecker.ExtractCommitHash("1.0.0"));
    }

    [Fact]
    public void ExtractCommitHash_PlusWithNothingAfterIt_ReturnsNull()
    {
        Assert.Null(BuildCurrencyChecker.ExtractCommitHash("1.0.0+"));
    }

    // --- CheckAsync: (a) fresh/current hash ---

    [Fact]
    public async Task CheckAsync_EmptyLogOutput_ReturnsCurrentAndNeverCallsDiff()
    {
        var runner = new FakeGitCommandRunner { LogResult = new GitCommandResult(0, string.Empty, string.Empty) };
        var checker = new BuildCurrencyChecker(runner);

        var result = await checker.CheckAsync(
            "abc1234567890abc1234567890abc1234567890",
            "C:\\repo\\publish\\Ingestion",
            relevantPaths: ["Drps.Ingestion", "Drps.Shared"]);

        Assert.Equal(BuildCurrencyStatus.Current, result.Status);
        Assert.Equal(0, result.StaleCommitCount);
        // Current means "nothing to report" - the diff call is only worth making once we
        // already know there's something stale to describe, so it must never fire here.
        var call = Assert.Single(runner.Calls);
        Assert.Equal("log", call[0]);
    }

    // --- CheckAsync: (b) stale hash ---

    [Fact]
    public async Task CheckAsync_NonEmptyLogOutput_ReturnsStaleWithCommitCountAndChangedPaths()
    {
        var runner = new FakeGitCommandRunner
        {
            LogResult = new GitCommandResult(
                0,
                "9be883d Reactivate FinnhubEarningsFeeder\nabc1234 some earlier related commit\n",
                string.Empty),
            DiffResult = new GitCommandResult(
                0,
                "Drps.Ingestion/Program.cs\nDrps.Ingestion/EarningsWorker.cs\n",
                string.Empty)
        };
        var checker = new BuildCurrencyChecker(runner);

        var result = await checker.CheckAsync(
            "abc1234567890abc1234567890abc1234567890",
            "C:\\repo\\publish\\Ingestion",
            relevantPaths: ["Drps.Ingestion", "Drps.Shared"]);

        Assert.Equal(BuildCurrencyStatus.Stale, result.Status);
        Assert.Equal(2, result.StaleCommitCount);
        Assert.Equal(
            new[] { "Drps.Ingestion/Program.cs", "Drps.Ingestion/EarningsWorker.cs" },
            result.ChangedPaths);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal("log", runner.Calls[0][0]);
        Assert.Equal("diff", runner.Calls[1][0]);
    }

    [Fact]
    public async Task CheckAsync_LogArguments_UseEmbeddedHashRangeAndRelevantPaths()
    {
        var runner = new FakeGitCommandRunner { LogResult = new GitCommandResult(0, string.Empty, string.Empty) };
        var checker = new BuildCurrencyChecker(runner);

        await checker.CheckAsync(
            "deadbeef",
            "C:\\repo\\publish\\Gate",
            relevantPaths: ["Drps.Gate", "Drps.Calculator", "Drps.Ingestion", "Drps.Ledger", "Drps.Shared"]);

        var call = runner.Calls[0];
        Assert.Equal(["log", "--oneline", "deadbeef..main", "--", "Drps.Gate", "Drps.Calculator", "Drps.Ingestion", "Drps.Ledger", "Drps.Shared"], call);
    }

    [Fact]
    public async Task CheckAsync_DiffFailsAfterLogSucceeds_StillReturnsStaleWithEmptyChangedPaths()
    {
        // Best-effort: the commit count from a successful `log` is real and worth reporting
        // even if the follow-up `diff` call itself fails for some reason.
        var runner = new FakeGitCommandRunner
        {
            LogResult = new GitCommandResult(0, "9be883d Reactivate FinnhubEarningsFeeder\n", string.Empty),
            DiffResult = new GitCommandResult(1, string.Empty, "some diff error")
        };
        var checker = new BuildCurrencyChecker(runner);

        var result = await checker.CheckAsync(
            "abc1234567890abc1234567890abc1234567890",
            "C:\\repo\\publish\\Ingestion",
            relevantPaths: ["Drps.Ingestion", "Drps.Shared"]);

        Assert.Equal(BuildCurrencyStatus.Stale, result.Status);
        Assert.Equal(1, result.StaleCommitCount);
        Assert.Empty(result.ChangedPaths!);
    }

    // --- CheckAsync: (c) git unreachable degrades to Unverifiable, never throws ---

    [Fact]
    public async Task CheckAsync_GitExecutableNotFound_ReturnsUnverifiableWithoutThrowing()
    {
        var runner = new FakeGitCommandRunner
        {
            ThrowOnRun = new System.ComponentModel.Win32Exception("The system cannot find the file specified")
        };
        var checker = new BuildCurrencyChecker(runner);

        // Calling directly (not wrapped in Assert.ThrowsAsync) is itself part of the assertion -
        // if CheckAsync let this exception propagate, the test would fail here with an
        // unhandled exception rather than reaching the Assert.Equal below.
        var result = await checker.CheckAsync(
            "abc1234567890abc1234567890abc1234567890",
            "C:\\repo\\publish\\Ingestion",
            relevantPaths: ["Drps.Ingestion", "Drps.Shared"]);

        Assert.Equal(BuildCurrencyStatus.Unverifiable, result.Status);
        Assert.Contains("system cannot find the file", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_GitLogNonZeroExit_ReturnsUnverifiableWithoutThrowing()
    {
        // A "bad revision" case - the embedded hash isn't reachable in this repo's history at
        // all (e.g. a rebase, or a build produced from a since-deleted branch).
        var runner = new FakeGitCommandRunner
        {
            LogResult = new GitCommandResult(128, string.Empty, "fatal: bad revision 'abc123..main'")
        };
        var checker = new BuildCurrencyChecker(runner);

        var result = await checker.CheckAsync(
            "abc123",
            "C:\\repo\\publish\\Ingestion",
            relevantPaths: ["Drps.Ingestion", "Drps.Shared"]);

        Assert.Equal(BuildCurrencyStatus.Unverifiable, result.Status);
        Assert.Contains("128", result.Detail);
        Assert.Contains("bad revision", result.Detail);
    }

    [Fact]
    public async Task CheckAsync_BlankEmbeddedHash_ReturnsUnverifiableWithoutCallingGit()
    {
        var runner = new FakeGitCommandRunner();
        var checker = new BuildCurrencyChecker(runner);

        var result = await checker.CheckAsync(
            null,
            "C:\\repo\\publish\\Ingestion",
            relevantPaths: ["Drps.Ingestion", "Drps.Shared"]);

        Assert.Equal(BuildCurrencyStatus.Unverifiable, result.Status);
        Assert.Empty(runner.Calls);
    }

    // --- BuildCurrencyAlerter ---

    [Fact]
    public async Task SendStaleBuildAlertAsync_MissingCredentials_ReturnsNotConfiguredAndMakesNoHttpCall()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("should never be called"));
        using var client = new HttpClient(handler);

        var result = await BuildCurrencyAlerter.SendStaleBuildAlertAsync(
            client, appToken: null, userKey: "some-user-key", "Drps.Ingestion", 2, CancellationToken.None);

        Assert.Equal(BuildCurrencyAlertOutcome.NotConfigured, result.Outcome);
    }

    [Fact]
    public async Task SendStaleBuildAlertAsync_SuccessfulPost_ReturnsSent()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        var result = await BuildCurrencyAlerter.SendStaleBuildAlertAsync(
            client, "app-token", "user-key", "Drps.Ingestion", 2, CancellationToken.None);

        Assert.Equal(BuildCurrencyAlertOutcome.Sent, result.Outcome);
    }

    [Fact]
    public async Task SendStaleBuildAlertAsync_NonSuccessHttpResponse_ReturnsHttpFailureWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid token")
        });
        using var client = new HttpClient(handler);

        var result = await BuildCurrencyAlerter.SendStaleBuildAlertAsync(
            client, "app-token", "user-key", "Drps.Ingestion", 2, CancellationToken.None);

        Assert.Equal(BuildCurrencyAlertOutcome.HttpFailure, result.Outcome);
        Assert.Contains("400", result.Detail);
    }

    [Fact]
    public async Task SendStaleBuildAlertAsync_HttpClientThrows_ReturnsExceptionWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("network unreachable"));
        using var client = new HttpClient(handler);

        var result = await BuildCurrencyAlerter.SendStaleBuildAlertAsync(
            client, "app-token", "user-key", "Drps.Ingestion", 2, CancellationToken.None);

        Assert.Equal(BuildCurrencyAlertOutcome.Exception, result.Outcome);
        Assert.Contains("network unreachable", result.Detail);
    }
}
