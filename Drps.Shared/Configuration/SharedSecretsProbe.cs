using System.Text.Json;

namespace Drps.Shared.Configuration;

/// <summary>
/// Closes a real, confirmed gap: Drps.Ingestion/Calculator/Gate's Program.cs each register
/// ~/.APIKeys/shared-APIKeys.json via `builder.Configuration.AddJsonFile(path, optional: true,
/// reloadOnChange: false)`. `optional: true` only suppresses the file-not-found case -
/// AddJsonFile's own actual read/parse is deferred until `builder.Build()`, which throws
/// uncaught if the file exists but is malformed JSON or locked/unreadable by another process
/// (confirmed empirically during the 2026-08-03/04 build-currency audit:
/// System.IO.InvalidDataException wrapping a JsonException for malformed content;
/// System.IO.IOException for a locked file). Program.cs's top-level statements have no
/// surrounding try/catch, so this crashes the whole process before host.Run() - before any
/// Worker/GateScanService is ever scheduled. This was a pre-existing risk in
/// Ingestion/Calculator since early in the project (this exact file has always been loaded via
/// AddJsonFile there) and was newly extended to Drps.Gate for the first time by the
/// 2026-08-03 build-currency self-check task, which gave Gate this same loading step for the
/// first time.
///
/// Fix: probe the file - read and parse it - BEFORE ever calling AddJsonFile, using the same
/// underlying parser (System.Text.Json.JsonDocument) Microsoft.Extensions.Configuration.Json
/// uses internally, so a file that probes clean here is faithfully guaranteed to load cleanly
/// via the real AddJsonFile call immediately afterward. If the probe fails, the caller simply
/// never calls AddJsonFile for this path - builder.Build() is then never exposed to this
/// failure mode at all, rather than being wrapped in a blanket try/catch that could also mask
/// an unrelated DI/config startup error. Missing-file behavior is completely unchanged (still
/// resolves via AddJsonFile's own optional:true, same as before this fix).
///
/// Known, accepted gap: a narrow time-of-check-to-time-of-use race exists between this probe
/// and AddJsonFile's own later read inside builder.Build() (e.g. the file could theoretically
/// become locked in the brief window between the two) - not solved here, same as it wasn't
/// solved by the pre-existing missing-file check AddJsonFile's own optional:true already relies
/// on. Out of scope: this closes the confirmed, reproducible crash, not a distributed-systems-
/// grade atomicity guarantee for a single local file.
/// </summary>
public static class SharedSecretsProbe
{
    public static SharedSecretsProbeResult Probe(string path)
    {
        if (!File.Exists(path))
        {
            return SharedSecretsProbeResult.NotFound();
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            return SharedSecretsProbeResult.Loadable();
        }
        catch (JsonException ex)
        {
            return SharedSecretsProbeResult.FailedToLoad($"malformed JSON - {ex.Message}");
        }
        catch (IOException ex)
        {
            return SharedSecretsProbeResult.FailedToLoad($"file locked or unreadable - {ex.Message}");
        }
    }
}
