using Drps.Shared.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Drps.Tests;

/// <summary>
/// Covers both layers of the 2026-08-04 fail-open fix: SharedSecretsProbe.Probe's own detection
/// logic in isolation, and - per the task's explicit requirement - the exact real-Host-builder
/// reproduction method the 2026-08-03/04 audit used to originally confirm the crash (
/// Host.CreateApplicationBuilder -> builder.Configuration.AddJsonFile -> builder.Build()), now
/// proving builder.Build() no longer throws for any of the three real scenarios. Drps.Ingestion/
/// Calculator/Gate's Program.cs each call SharedSecretsProbe.Probe with this exact same
/// probe-then-conditionally-register sequence - since Program.cs itself is top-level statements
/// with no dedicated test file anywhere in this codebase (same non-testability boundary already
/// true of its pre-existing "shared secrets file...loaded" log line), this suite is what "per
/// project" resolves to: one thorough proof of the shared sequence every project's Program.cs
/// follows identically, rather than three copies of the same assertion under different names.
/// </summary>
public class SharedSecretsProbeTests
{
    private static string CreateTempFile(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        return path;
    }

    // --- SharedSecretsProbe.Probe: pure detection logic ---

    [Fact]
    public void Probe_FileDoesNotExist_ReturnsNotFound()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");

        var result = SharedSecretsProbe.Probe(path);

        Assert.Equal(SharedSecretsProbeStatus.NotFound, result.Status);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void Probe_ValidJson_ReturnsLoadable()
    {
        var path = CreateTempFile("""{"Pushover": {"AppToken": "x", "UserKey": "y"}}""");
        try
        {
            var result = SharedSecretsProbe.Probe(path);

            Assert.Equal(SharedSecretsProbeStatus.Loadable, result.Status);
            Assert.Null(result.FailureReason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Probe_MalformedJson_ReturnsFailedToLoadWithReason()
    {
        var path = CreateTempFile("not valid json {{{");
        try
        {
            var result = SharedSecretsProbe.Probe(path);

            Assert.Equal(SharedSecretsProbeStatus.FailedToLoad, result.Status);
            Assert.Contains("malformed JSON", result.FailureReason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Probe_FileLockedByAnotherHandle_ReturnsFailedToLoadWithReason()
    {
        var path = CreateTempFile("""{"Pushover": {"AppToken": "x"}}""");
        try
        {
            // Same reproduction shape the audit used: an exclusive (FileShare.None) handle held
            // by "another process" - here, another handle within this same test process, which
            // is indistinguishable from Probe's point of view.
            using var exclusiveLock = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var result = SharedSecretsProbe.Probe(path);

            Assert.Equal(SharedSecretsProbeStatus.FailedToLoad, result.Status);
            Assert.Contains("locked or unreadable", result.FailureReason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- Real Host.CreateApplicationBuilder reproduction, matching the audit's own method ---
    //
    // Exact same sequence every Program.cs uses:
    //   var result = SharedSecretsProbe.Probe(path);
    //   if (result.Status != SharedSecretsProbeStatus.FailedToLoad)
    //       builder.Configuration.AddJsonFile(path, optional: true, reloadOnChange: false);
    //   var host = builder.Build();

    [Fact]
    public void RealHostBuilder_MissingFile_BuildsSuccessfully()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");

        var probeResult = SharedSecretsProbe.Probe(path);
        var builder = Host.CreateApplicationBuilder();
        if (probeResult.Status != SharedSecretsProbeStatus.FailedToLoad)
        {
            builder.Configuration.AddJsonFile(path, optional: true, reloadOnChange: false);
        }

        using var host = builder.Build();

        Assert.Equal(SharedSecretsProbeStatus.NotFound, probeResult.Status);
    }

    [Fact]
    public void RealHostBuilder_MalformedJsonFile_NoLongerThrows_BuildsSuccessfully()
    {
        // Before the 2026-08-04 fix, this exact sequence (skipping the Probe/if guard and
        // calling AddJsonFile unconditionally) reproduced the real, confirmed crash:
        // builder.Build() threw System.IO.InvalidDataException (inner FormatException/
        // JsonException: "Could not parse the JSON file."), uncaught, before host.Run() and
        // before any Worker was ever scheduled.
        var path = CreateTempFile("not valid json {{{");
        try
        {
            var probeResult = SharedSecretsProbe.Probe(path);
            var builder = Host.CreateApplicationBuilder();
            if (probeResult.Status != SharedSecretsProbeStatus.FailedToLoad)
            {
                builder.Configuration.AddJsonFile(path, optional: true, reloadOnChange: false);
            }

            using var host = builder.Build();

            Assert.Equal(SharedSecretsProbeStatus.FailedToLoad, probeResult.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RealHostBuilder_LockedFile_NoLongerThrows_BuildsSuccessfully()
    {
        // Before the fix, this reproduced: builder.Build() threw System.IO.IOException
        // ("...being used by another process"), uncaught, same crash-before-host.Run() outcome.
        var path = CreateTempFile("""{"Pushover": {"AppToken": "x"}}""");
        try
        {
            using var exclusiveLock = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var probeResult = SharedSecretsProbe.Probe(path);
            var builder = Host.CreateApplicationBuilder();
            if (probeResult.Status != SharedSecretsProbeStatus.FailedToLoad)
            {
                builder.Configuration.AddJsonFile(path, optional: true, reloadOnChange: false);
            }

            using var host = builder.Build();

            Assert.Equal(SharedSecretsProbeStatus.FailedToLoad, probeResult.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RealHostBuilder_ValidFile_StillLoadsIntoConfiguration()
    {
        // Confirms the fix doesn't accidentally suppress a genuinely valid file too - a valid
        // file must still actually be registered and readable via IConfiguration afterward, not
        // just "not crash."
        var path = CreateTempFile("""{"Pushover": {"AppToken": "real-token-value"}}""");
        try
        {
            var probeResult = SharedSecretsProbe.Probe(path);
            var builder = Host.CreateApplicationBuilder();
            if (probeResult.Status != SharedSecretsProbeStatus.FailedToLoad)
            {
                builder.Configuration.AddJsonFile(path, optional: true, reloadOnChange: false);
            }

            using var host = builder.Build();

            Assert.Equal(SharedSecretsProbeStatus.Loadable, probeResult.Status);
            Assert.Equal("real-token-value", host.Services.GetRequiredService<IConfiguration>()["Pushover:AppToken"]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
