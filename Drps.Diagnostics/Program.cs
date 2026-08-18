using System.Diagnostics;
using System.Net.Http.Headers;
using Drps.Diagnostics;
using Microsoft.Extensions.Configuration;

// Dispatch: default (no args) preserves this project's existing, already-documented entry
// point ("Run via: dotnet run --project Drps.Diagnostics") unchanged - the Alpaca bandwidth
// probe. A new, explicit "regime-distribution" verb runs the second, independent probe added
// for CLAUDE.md's "Regime Distribution Analysis" task (2026-07-28) - same named-opt-in
// convention this codebase already uses elsewhere (e.g. --run-now=<WorkerName>), rather than
// silently changing what the bare command does. "tickerbot-verify" (CLAUDE.md's "TickerBot
// Verification Tool," 2026-08-06) follows the identical convention for a third, independent
// probe.
if (args.Length > 0 && string.Equals(args[0], "regime-distribution", StringComparison.OrdinalIgnoreCase))
{
    return await RunRegimeDistributionAnalysisAsync();
}

if (args.Length > 0 && string.Equals(args[0], "tickerbot-verify", StringComparison.OrdinalIgnoreCase))
{
    return await RunTickerBotVerificationAsync();
}

return await RunAlpacaBandwidthProbeAsync();

// Standalone empirical bandwidth probe against Alpaca's real /v2/stocks/bars multi-symbol
// endpoint - answers the "Empirical verification steps, not yet performed" list under
// CLAUDE.md's "Two-Tier Verification Cost Model" block. Deliberately NOT wired into
// Drps.Ingestion's Worker/IngestionRunner - this is a one-time finding-gathering run, not a
// scheduled job, and writes nothing to any database. Run via: dotnet run --project Drps.Diagnostics
static async Task<int> RunAlpacaBandwidthProbeAsync()
{
    // Same shared-secrets file every other DRPS project reads from (see CLAUDE.md's "Twelve Data
    // Ruled Out..." block for why this file, not Key Vault, is the current source of truth).
    var config = new ConfigurationBuilder()
        .AddJsonFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".APIKeys", "shared-APIKeys.json"),
            optional: true)
        .Build();

    var keyId = config["Alpaca:KeyId"];
    var secretKey = config["Alpaca:SecretKey"];

    if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(secretKey))
    {
        Console.Error.WriteLine(
            "Alpaca:KeyId / Alpaca:SecretKey not found in %USERPROFILE%\\.APIKeys\\shared-APIKeys.json - " +
            "same credential source every other DRPS project reads from. Nothing to test without a real key.");
        return 1;
    }

    var logPath = Path.Combine(AppContext.BaseDirectory, "Logs", $"alpaca-bandwidth-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    using var log = new DiagnosticLog(logPath);

    log.Info("=== DRPS Alpaca Bandwidth Diagnostic ===");
    log.Info($"Log file: {logPath}");

    using var httpClient = new HttpClient { BaseAddress = new Uri("https://data.alpaca.markets") };
    httpClient.DefaultRequestHeaders.Add("APCA-API-KEY-ID", keyId);
    httpClient.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", secretKey);
    httpClient.Timeout = TimeSpan.FromMinutes(3);

    log.Info("Fetching real ticker universe from NASDAQ Trader symbol directory files...");
    IReadOnlyList<string> universe;
    try
    {
        using var universeHttpClient = new HttpClient();
        universe = await UniverseSymbolLoader.LoadRealSymbolsAsync(universeHttpClient, CancellationToken.None);
    }
    catch (Exception ex)
    {
        log.Warn($"Universe fetch failed: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
    log.Info($"Loaded {universe.Count} real, currently-listed equity symbols (after filtering test issues and non-plain tickers).");

    const int minUniverseNeeded = 3000; // Stage 1 (up to 2000) + a disjoint Stage 2 sample of ~750
    if (universe.Count < minUniverseNeeded)
    {
        log.Warn(
            $"Universe only has {universe.Count} symbols after filtering - fewer than the " +
            $"{minUniverseNeeded} needed to run fully disjoint Stage 1/Stage 2 samples. Proceeding " +
            "with what's available; later stages may run smaller than intended.");
    }

    var probe = new AlpacaBandwidthProbe(httpClient, log);

    // --- Stage 1: batch-size escalation -----------------------------------------------------
    // 14 calendar days - deliberately wider than Stage 2's window, so a 2000-symbol batch has a
    // real chance of crossing the 10,000-data-point response cap (limit=10000 above) and this
    // stage can observe genuine next_page_token behavior, not just the symbol-count ceiling alone.
    var batchTestEnd = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
    var batchTestStart = batchTestEnd.AddDays(-14);
    int[] batchSizes = [200, 500, 1000, 1500, 2000];

    log.Info("");
    log.Info($"=== Stage 1: Batch-size escalation ({batchTestStart:yyyy-MM-dd} to {batchTestEnd:yyyy-MM-dd}) ===");

    var batchResults = new List<BatchProbeResult>();
    var universeOffset = 0;
    foreach (var size in batchSizes)
    {
        if (universeOffset + size > universe.Count)
        {
            log.Warn($"Skipping batch size {size} - not enough remaining universe symbols ({universe.Count - universeOffset} left).");
            continue;
        }

        var batch = universe.Skip(universeOffset).Take(size).ToList();
        universeOffset += size;

        log.Info($"Requesting {size} symbols in one call...");
        var result = await probe.ProbeBatchAsync(batch, batchTestStart, batchTestEnd, CancellationToken.None);
        batchResults.Add(result);

        if (result.Success)
        {
            // A shortfall here is ambiguous in principle (legitimately no data vs. real
            // truncation) but Stage 1's 14-day window makes a large gap far more likely to be
            // genuine truncation than legitimate absence for a currently-listed equity - flagged
            // explicitly in the summary below rather than silently trusted either way.
            log.Info(
                $"  -> HTTP {result.HttpStatusCode}, {result.Duration.TotalSeconds:F2}s, " +
                $"{result.SymbolsWithBarsReturned}/{size} symbols returned bars, " +
                $"next_page_token={(result.NextPageTokenPresent ? "PRESENT (pagination required)" : "absent")}");
        }
        else
        {
            log.Info($"  -> FAILED: HTTP {result.HttpStatusCode?.ToString() ?? "n/a"} - {result.ErrorMessage}");
        }

        foreach (var (headerName, headerValue) in result.RateLimitHeaders)
            log.Info($"     header: {headerName} = {headerValue}");
    }

    var largestCleanBatch = batchResults
        .Where(r => r.Success && !r.NextPageTokenPresent)
        .Select(r => r.RequestedSymbolCount)
        .DefaultIfEmpty(0)
        .Max();

    // --- Stage 2: timed representative sweep ------------------------------------------------
    // A narrow 3-calendar-day window - representative of a real nightly incremental fetch (one
    // new trading day's bar per ticker), unlike Stage 1's intentionally-wide window. Chunked at
    // whichever batch size Stage 1 actually confirmed clean, falling back to 200 (the older,
    // more conservative documented figure) if nothing in Stage 1 came back clean.
    var sweepChunkSize = largestCleanBatch > 0 ? largestCleanBatch : 200;
    const int sweepTargetTickerCount = 750;

    var sweepEnd = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
    var sweepStart = sweepEnd.AddDays(-3);

    log.Info("");
    log.Info($"=== Stage 2: Timed representative sweep ({sweepStart:yyyy-MM-dd} to {sweepEnd:yyyy-MM-dd}, chunk size {sweepChunkSize}) ===");

    var sweepUniverse = universe.Skip(universeOffset).Take(sweepTargetTickerCount).ToList();
    if (sweepUniverse.Count == 0)
    {
        log.Warn("No disjoint universe symbols left for Stage 2 - reusing Stage 1's pool instead.");
        sweepUniverse = universe.Take(sweepTargetTickerCount).ToList();
    }

    var sweepStopwatch = Stopwatch.StartNew();
    var sweepCallCount = 0;
    var sweepFailureCount = 0;
    foreach (var chunk in Chunk(sweepUniverse, sweepChunkSize))
    {
        sweepCallCount++;
        var result = await probe.ProbeBatchAsync(chunk, sweepStart, sweepEnd, CancellationToken.None);
        if (!result.Success)
        {
            sweepFailureCount++;
            log.Warn($"  chunk {sweepCallCount} ({chunk.Count} symbols) failed: HTTP {result.HttpStatusCode?.ToString() ?? "n/a"} - {result.ErrorMessage}");
        }
        else
        {
            log.Info($"  chunk {sweepCallCount} ({chunk.Count} symbols): {result.Duration.TotalSeconds:F2}s, {result.SymbolsWithBarsReturned} symbols returned bars");
        }
    }
    sweepStopwatch.Stop();

    const int fullUniverseSize = 10073; // CLAUDE.md's stated current universe size
    var extrapolatedFullSweepSeconds = sweepUniverse.Count > 0
        ? sweepStopwatch.Elapsed.TotalSeconds / sweepUniverse.Count * fullUniverseSize
        : 0;
    var extrapolatedFullSweepCallCount = (int)Math.Ceiling(fullUniverseSize / (double)sweepChunkSize);

    // --- Summary ------------------------------------------------------------------------------
    log.Info("");
    log.Info("=== SUMMARY ===");
    log.Info($"Confirmed largest clean batch size (200-2000 tested, no error, no next_page_token): " +
              $"{(largestCleanBatch > 0 ? largestCleanBatch.ToString() : "NONE - even the smallest tested size failed or paginated")}");
    log.Info($"Batch sizes tested: {string.Join(", ", batchResults.Select(r => r.RequestedSymbolCount))}");
    foreach (var r in batchResults)
    {
        var outcome = !r.Success ? $"FAILED ({r.HttpStatusCode?.ToString() ?? "n/a"})"
            : r.NextPageTokenPresent ? "SUCCEEDED but required pagination"
            : r.SymbolsWithBarsReturned < r.RequestedSymbolCount ? $"SUCCEEDED but only {r.SymbolsWithBarsReturned}/{r.RequestedSymbolCount} symbols returned bars (possible truncation)"
            : "SUCCEEDED, clean";
        log.Info($"  - {r.RequestedSymbolCount} symbols: {outcome} in {r.Duration.TotalSeconds:F2}s");
    }

    var allObservedRateLimitHeaders = batchResults
        .SelectMany(r => r.RateLimitHeaders)
        .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
    if (allObservedRateLimitHeaders.Count > 0)
    {
        log.Info("Confirmed rate-limit headers observed (most recent value per header name, across Stage 1 calls):");
        foreach (var (name, value) in allObservedRateLimitHeaders)
            log.Info($"  - {name}: {value}");
    }
    else
    {
        log.Info("Confirmed rate-limit ceiling: NO headers matching '*ratelimit*'/'*rate-limit*' were observed in any Stage 1 response.");
    }

    log.Info($"Timed sweep: {sweepUniverse.Count} real tickers across {sweepCallCount} calls ({sweepFailureCount} failed), chunk size {sweepChunkSize}, wall-clock {sweepStopwatch.Elapsed.TotalSeconds:F1}s");
    log.Info($"Extrapolated full ~{fullUniverseSize}-ticker sweep: ~{extrapolatedFullSweepSeconds:F0}s (~{extrapolatedFullSweepSeconds / 60:F1} min) across ~{extrapolatedFullSweepCallCount} calls at chunk size {sweepChunkSize}");
    log.Info("");
    log.Info("These are one-time empirical findings only - nothing here is persisted to any");
    log.Info("production table, and no batch-size constant should be locked into production code");
    log.Info("until these numbers have been reviewed (see CLAUDE.md: Two-Tier Verification Cost Model).");

    return 0;
}

// Standalone, reproducible empirical distribution analysis for VXN/VIX and VIX/VIX3M ratios -
// built to close the gap CLAUDE.md's 2026-07-28 audit found: the median/P10/P90/max figures
// locked into CLAUDE.md's "Regime Thresholds"/"Regime Multiplier" decision blocks (2026-07-26)
// had no underlying script, data pull, or computation anywhere in the repo. Writes nothing to
// any database - pulls real Cboe/FRED CSVs, computes statistics, and saves a committed,
// re-runnable markdown report. Run via: dotnet run --project Drps.Diagnostics -- regime-distribution
static async Task<int> RunRegimeDistributionAnalysisAsync()
{
    var logPath = Path.Combine(AppContext.BaseDirectory, "Logs", $"regime-distribution-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    using var log = new DiagnosticLog(logPath);

    log.Info("=== DRPS Regime Distribution Analysis (VXN/VIX, VIX/VIX3M) ===");
    log.Info($"Log file: {logPath}");

    log.Info("Fetching VIX (Cboe direct, sole source, full history)...");
    IReadOnlyDictionary<DateOnly, decimal> vix;
    try
    {
        vix = await FetchWithRetryAsync(
            log, "VIX/Cboe",
            client => RegimeSeriesFetcher.FetchCboeCloseSeriesAsync(
                client, "https://cdn.cboe.com/api/global/us_indices/daily_prices/VIX_History.csv", CancellationToken.None));
    }
    catch (Exception ex)
    {
        log.Warn($"VIX fetch failed after all retries: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
    log.Info($"  -> {vix.Count} trading days, {vix.Keys.Min():yyyy-MM-dd} through {vix.Keys.Max():yyyy-MM-dd}");

    // FRED fetched via curl.exe, not HttpClient - see CurlHttpFetcher's own doc comment for the
    // empirical evidence gathered during this task (roughly a dozen live trials: HttpClient
    // hung or had its connection reset in the clear majority; curl.exe against the identical
    // URLs succeeded in 1-2 seconds every time it was tried, including immediately after an
    // HttpClient failure against the same URL). CSV parsing itself is unchanged - only the
    // transport differs from the VIX/Cboe fetch above.
    log.Info("Fetching VXN (FRED VXNCLS, depth source, via curl.exe)...");
    IReadOnlyDictionary<DateOnly, decimal> vxn;
    try
    {
        vxn = await FetchFredViaCurlWithRetryAsync(log, "https://fred.stlouisfed.org/graph/fredgraph.csv?id=VXNCLS", "VXNCLS");
    }
    catch (Exception ex)
    {
        log.Warn($"VXN fetch failed after all retries: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
    log.Info($"  -> {vxn.Count} trading days, {vxn.Keys.Min():yyyy-MM-dd} through {vxn.Keys.Max():yyyy-MM-dd}");

    log.Info("Fetching VIX3M (FRED VXVCLS, depth source, via curl.exe)...");
    IReadOnlyDictionary<DateOnly, decimal> vix3m;
    try
    {
        vix3m = await FetchFredViaCurlWithRetryAsync(log, "https://fred.stlouisfed.org/graph/fredgraph.csv?id=VXVCLS", "VXVCLS");
    }
    catch (Exception ex)
    {
        log.Warn($"VIX3M fetch failed after all retries: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
    log.Info($"  -> {vix3m.Count} trading days, {vix3m.Keys.Min():yyyy-MM-dd} through {vix3m.Keys.Max():yyyy-MM-dd}");

    // Ratios are only computed on dates where BOTH series have a real value - an inner join,
    // not a forward-filled/interpolated approximation. A date missing from either side (a real
    // FRED-vs-Cboe publication-calendar mismatch, distinct from FRED's own already-filtered
    // holiday rows) is simply excluded from that ratio's sample.
    var vxnVixRatios = new List<(DateOnly Date, double Value)>();
    foreach (var (date, vxnClose) in vxn)
    {
        if (vix.TryGetValue(date, out var vixClose) && vixClose != 0)
            vxnVixRatios.Add((date, (double)(vxnClose / vixClose)));
    }

    var vixVix3mRatios = new List<(DateOnly Date, double Value)>();
    foreach (var (date, vixClose) in vix)
    {
        if (vix3m.TryGetValue(date, out var vix3mClose) && vix3mClose != 0)
            vixVix3mRatios.Add((date, (double)(vixClose / vix3mClose)));
    }

    log.Info("");
    log.Info($"VXN/VIX matched ratio sample: {vxnVixRatios.Count} trading days");
    log.Info($"VIX/VIX3M matched ratio sample: {vixVix3mRatios.Count} trading days");

    if (vxnVixRatios.Count == 0 || vixVix3mRatios.Count == 0)
    {
        log.Warn("One or both ratio samples are empty - cannot compute statistics. Aborting without writing a report.");
        return 1;
    }

    var vxnVixStats = DistributionStatistics.Compute(vxnVixRatios);
    var vixVix3mStats = DistributionStatistics.Compute(vixVix3mRatios);

    log.Info("");
    log.Info("=== VXN/VIX statistics ===");
    LogStatistics(log, vxnVixStats);
    log.Info("");
    log.Info("=== VIX/VIX3M statistics ===");
    LogStatistics(log, vixVix3mStats);

    var vixCoverage = new SeriesCoverage("VIX", "Cboe direct", vix.Count, vix.Keys.Min(), vix.Keys.Max());
    var vxnCoverage = new SeriesCoverage("VXN", "FRED (VXNCLS)", vxn.Count, vxn.Keys.Min(), vxn.Keys.Max());
    var vix3mCoverage = new SeriesCoverage("VIX3M", "FRED (VXVCLS)", vix3m.Count, vix3m.Keys.Min(), vix3m.Keys.Max());

    var generatedDate = DateOnly.FromDateTime(DateTime.Now);
    var report = RegimeDistributionReportWriter.BuildReport(
        generatedDate,
        vixCoverage, vxnCoverage, vix3mCoverage,
        vxnVixRatios.Count, vxnVixStats,
        vixVix3mRatios.Count, vixVix3mStats);

    // Dated by the ACTUAL run date, not a literal hardcoded string - matches this repo's own
    // established convention for dated report files (e.g. CapitalFill-Discovery-Audit-
    // 2026-07-26.md) of one new dated file per day it's regenerated, rather than one fixed
    // filename silently overwritten on every future run regardless of when it was produced.
    var reportPath = Path.Combine(FindRepoRoot(), $"Regime-Distribution-Analysis-{generatedDate:yyyy-MM-dd}.md");
    await File.WriteAllTextAsync(reportPath, report);

    log.Info("");
    log.Info($"Report written to: {reportPath}");

    return 0;
}

static void LogStatistics(DiagnosticLog log, DistributionStatistics stats)
{
    log.Info($"  Count:              {stats.Count}");
    log.Info($"  Mean:               {stats.Mean:F4}");
    log.Info($"  Median:             {stats.Median:F4}");
    log.Info($"  Standard deviation: {stats.StandardDeviation:F4}");
    log.Info($"  Skewness:           {stats.Skewness:F4}");
    log.Info($"  Min:                {stats.Min:F4} ({stats.MinDate:yyyy-MM-dd})");
    log.Info($"  Max:                {stats.Max:F4} ({stats.MaxDate:yyyy-MM-dd})");
    foreach (var p in new[] { 10, 25, 50, 75, 90, 95, 99 })
        log.Info($"  P{p,-3}:              {stats.Percentiles[p]:F4}");
}

// 5 attempts, a fresh HttpClient per attempt (a stuck/half-reset connection from a prior
// attempt must not carry over), 90-second per-attempt timeout (matches production's
// FredRegimeClient registration exactly), short fixed backoff between attempts. Empirically
// justified by this task's own testing, not a guessed number - both FRED endpoints exercised
// during this task swung between sub-second responses and full hangs/connection resets across
// consecutive runs with no code change, so a single generous timeout does not reliably solve
// this; genuine retries do.
static async Task<T> FetchWithRetryAsync<T>(DiagnosticLog log, string label, Func<HttpClient, Task<T>> fetch)
{
    const int maxAttempts = 5;
    var delay = TimeSpan.FromSeconds(5);

    for (var attempt = 1; ; attempt++)
    {
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(90);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DRPS-Diagnostics/1.0");

        try
        {
            return await fetch(httpClient);
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            log.Warn(
                $"  [{label}] attempt {attempt}/{maxAttempts} failed: {ex.GetType().Name}: {ex.Message} " +
                $"(inner: {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}) - retrying in {delay.TotalSeconds:F0}s");
            await Task.Delay(delay);
            delay += TimeSpan.FromSeconds(5);
        }
        // No catch for the final attempt (attempt == maxAttempts) - a real failure there must
        // propagate to the caller, not be silently swallowed by an unreachable fallback.
    }
}

// curl.exe-backed retry, kept separate from the generic HttpClient-based FetchWithRetryAsync
// above rather than forced into the same signature - curl still fails occasionally (real,
// transient network blips), so this keeps the same retry discipline, just via
// CurlHttpFetcher/RegimeSeriesFetcher.ParseFredCsv instead of an HttpClient callback.
static async Task<IReadOnlyDictionary<DateOnly, decimal>> FetchFredViaCurlWithRetryAsync(DiagnosticLog log, string url, string seriesId)
{
    const int maxAttempts = 5;
    var delay = TimeSpan.FromSeconds(5);

    for (var attempt = 1; ; attempt++)
    {
        try
        {
            var csv = await CurlHttpFetcher.FetchStringAsync(url, timeoutSeconds: 60, CancellationToken.None);
            return RegimeSeriesFetcher.ParseFredCsv(csv, seriesId);
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            log.Warn(
                $"  [{seriesId}/FRED via curl] attempt {attempt}/{maxAttempts} failed: {ex.GetType().Name}: {ex.Message} " +
                $"- retrying in {delay.TotalSeconds:F0}s");
            await Task.Delay(delay);
            delay += TimeSpan.FromSeconds(5);
        }
    }
}

// One-time TickerBot-vs-DRPS OHLCV cross-verification audit (CLAUDE.md's "TickerBot
// Verification Tool: Diagnostics Isolation Preserved Over Logic Reuse," 2026-08-06, extended
// 2026-08-14 for a full three-way OHLCV accuracy audit against a mixed 25-ticker sample: 15
// randomly selected from RollingDmaStates' current DMA-5-aligned candidates, 5 large-cap
// stable names, 5 small/micro-cap names likely to have thinner data coverage). Deliberately
// NOT DI-registered anywhere, NOT added to any Task Scheduler job, and NOT invoked from any
// other DRPS project - same standalone-tool posture as the two probes above, extended to data
// access too (see TickerBotVerificationProbe.cs's own doc comment for the SqlClient-not-EF
// reasoning). Reads TickerBot's key from the same shared secrets file every other DRPS
// project reads, and never logs, prints, or echoes it anywhere in this function or in
// TickerBotVerificationProbe - not even in an error message. Run via:
// dotnet run --project Drps.Diagnostics -- tickerbot-verify
static async Task<int> RunTickerBotVerificationAsync()
{
    var config = new ConfigurationBuilder()
        .AddJsonFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".APIKeys", "shared-APIKeys.json"),
            optional: true)
        .Build();

    // NOTE (2026-08-14): the real shared-APIKeys.json stores this credential under
    // "TickerBot:tickerbot-key", not "TickerBot:ApiKey" - confirmed directly against the real
    // file's property names (values never inspected/printed). Corrected here; this is a
    // config-path fix only, not a change to any secret value.
    var apiKey = config["TickerBot:tickerbot-key"];
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        Console.Error.WriteLine(
            "TickerBot:tickerbot-key not found in %USERPROFILE%\\.APIKeys\\shared-APIKeys.json - same credential " +
            "source every other DRPS project reads from. Nothing to verify without a real key.");
        return 1;
    }

    var logPath = Path.Combine(AppContext.BaseDirectory, "Logs", $"tickerbot-verify-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    using var log = new DiagnosticLog(logPath);

    log.Info("=== DRPS TickerBot OHLCV Accuracy Audit ===");
    log.Info($"Log file: {logPath}");
    // Confirms the key was found without ever printing its value - same "found and loaded"
    // convention every other DRPS Program.cs uses for its own shared-secrets load.
    log.Info("TickerBot:tickerbot-key found and loaded (value never logged).");

    // Same fallback connection-string literal every other DRPS Program.cs uses - hardcoded
    // rather than read from a project reference, matching this project's isolation invariant.
    const string connectionString = @"Server=(localdb)\mssqllocaldb;Database=Drps;Trusted_Connection=True;";

    // Fixed, per this task's own request - not the DMA-5-aligned pool, so a real ticker
    // already in that pool can't accidentally double up under two categories.
    string[] largeCap = ["AAPL", "MSFT", "JPM", "XOM", "JNJ"];

    // Thinly-traded/volatile microcap names, chosen from tickers CLAUDE.md's own history
    // already documents as low-priced/thin-liquidity real cases (GPRO, PLUG, FCEL, CIFR,
    // SOUN), not guessed blind - deliberately still-listed/actively-trading names, unlike
    // e.g. the already-delisted EXPR also mentioned there, which would trivially have zero
    // recent coverage on every source and add nothing to this audit.
    string[] smallCap = ["GPRO", "PLUG", "FCEL", "CIFR", "SOUN"];

    List<string> dmaAlignedSample;
    try
    {
        dmaAlignedSample = await SelectRandomDma5AlignedTickersAsync(connectionString, 15, largeCap.Concat(smallCap), CancellationToken.None);
    }
    catch (Exception ex)
    {
        log.Warn($"Failed to load RollingDmaStates DMA-5-aligned candidates: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }

    if (dmaAlignedSample.Count < 15)
    {
        log.Warn($"Only {dmaAlignedSample.Count} DMA-5-aligned candidate(s) available (fewer than the requested 15) - proceeding with what's available.");
    }
    log.Info($"DMA-5-aligned sample ({dmaAlignedSample.Count}): {string.Join(", ", dmaAlignedSample)}");
    log.Info($"Large-cap sample ({largeCap.Length}): {string.Join(", ", largeCap)}");
    log.Info($"Small/micro-cap sample ({smallCap.Length}): {string.Join(", ", smallCap)}");

    var sample = new List<(string Ticker, string Category)>();
    sample.AddRange(dmaAlignedSample.Select(t => (t, "DMA5-Aligned")));
    sample.AddRange(largeCap.Select(t => (t, "LargeCap")));
    sample.AddRange(smallCap.Select(t => (t, "SmallCap")));

    using var httpClient = new HttpClient { BaseAddress = new Uri("https://api.tickerbot.io") };
    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    httpClient.Timeout = TimeSpan.FromMinutes(5);

    var probe = new TickerBotVerificationProbe(httpClient, connectionString, log);

    const int tradingDayWindow = 90;

    TickerBotVerificationSummary summary;
    try
    {
        summary = await probe.RunAsync(sample, tradingDayWindow, CancellationToken.None);
    }
    catch (Exception ex)
    {
        log.Warn($"TickerBot verification run failed: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }

    log.Info("");
    log.Info("=== PER-TICKER RESULTS ===");
    foreach (var r in summary.TickerResults)
    {
        log.Info("");
        log.Info($"--- {r.Ticker} ({r.Category}) ---");

        if (r.SkipReason is not null)
        {
            log.Info($"  SKIPPED: {r.SkipReason}");
            continue;
        }

        log.Info($"  Window: {r.WindowStart:yyyy-MM-dd} .. {r.WindowEnd:yyyy-MM-dd}");
        log.Info($"  Local date counts: Alpaca={r.AlpacaDateCount} Tiingo={r.TiingoDateCount} TickerBot={r.TickerBotDateCount}");
        log.Info($"  Matching dates:    {r.MatchingDates}");
        log.Info($"  Mismatched dates:  {r.MismatchedDates}");

        if (r.MismatchDetails.Count > 0)
        {
            log.Info("  Mismatch detail:");
            foreach (var detail in r.MismatchDetails)
                log.Info($"    {detail}");
        }

        if (r.CoverageGapDates.Count > 0)
        {
            log.Info($"  COVERAGE GAP - TickerBot missing {r.CoverageGapDates.Count} date(s) both Alpaca and Tiingo have:");
            foreach (var date in r.CoverageGapDates)
                log.Info($"    {date:yyyy-MM-dd}");
        }

        if (r.PossibleQualityIssueDates.Count > 0)
        {
            log.Info($"  POSSIBLE QUALITY ISSUE - TickerBot has {r.PossibleQualityIssueDates.Count} date(s) neither Alpaca nor Tiingo has:");
            foreach (var date in r.PossibleQualityIssueDates)
                log.Info($"    {date:yyyy-MM-dd}");
        }
    }

    log.Info("");
    log.Info("=== SAMPLE-WIDE TOTALS ===");
    var audited = summary.TickerResults.Where(r => r.SkipReason is null).ToList();
    var skipped = summary.TickerResults.Where(r => r.SkipReason is not null).ToList();
    log.Info($"Tickers audited: {audited.Count}, skipped: {skipped.Count} (of {summary.TickerResults.Count} total)");
    log.Info($"Total matching dates:   {audited.Sum(r => r.MatchingDates)}");
    log.Info($"Total mismatched dates: {audited.Sum(r => r.MismatchedDates)}");
    log.Info($"Total coverage gaps:    {audited.Sum(r => r.CoverageGapDates.Count)}");
    log.Info($"Total possible quality issues: {audited.Sum(r => r.PossibleQualityIssueDates.Count)}");
    if (skipped.Count > 0)
    {
        log.Info("Skipped tickers:");
        foreach (var r in skipped)
            log.Info($"  {r.Ticker} ({r.Category}): {r.SkipReason}");
    }

    return 0;
}

// Reads RollingDmaStates (Drps.Calculator-owned table, no ProjectReference held to that
// project - same raw-SqlClient isolation as TickerBotVerificationProbe itself) for the
// current DMA-5-aligned candidate pool, matching Drps.Ingestion/Orchestration/
// SqlDma5AlignedCandidateSource.cs's exact query shape (including its own hand-duplicated
// CalculationVersion=1 constant - same "update by hand if the source ever changes" precedent
// that file's own doc comment already establishes). Excludes the caller's fixed
// large-cap/small-cap tickers from the pool first, so a real overlap can't silently double-
// count a ticker under two sample categories, then randomly selects up to `count` of what's
// left. Returns fewer than `count` (never fabricated up to it) if the pool itself is smaller.
static async Task<List<string>> SelectRandomDma5AlignedTickersAsync(
    string connectionString, int count, IEnumerable<string> excludeTickers, CancellationToken cancellationToken)
{
    const int dmaAlignmentTerm5 = 5; // Drps.Shared.Models.DmaAlignmentTerm.Term5
    const int calculationVersion = 1; // RollingDmaStateService.CalculationVersion, current value

    const string sql = """
        SELECT DISTINCT Ticker
        FROM dbo.RollingDmaStates
        WHERE Term = @Term
            AND IsAligned = 1
            AND CalculationVersion = @CalculationVersion
        """;

    var pool = new List<string>();

    await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
    command.Parameters.AddWithValue("@Term", dmaAlignmentTerm5);
    command.Parameters.AddWithValue("@CalculationVersion", calculationVersion);

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
        pool.Add(reader.GetString(0));

    var excludeSet = new HashSet<string>(excludeTickers, StringComparer.OrdinalIgnoreCase);
    pool = pool.Where(t => !excludeSet.Contains(t)).ToList();

    // Full Fisher-Yates shuffle via Random.Shared, then take the first `count` - no fixed
    // seed, per the task's own "randomly selected" wording; the actual selected tickers are
    // logged by the caller so the run remains reviewable after the fact even though it isn't
    // reproducible bit-for-bit.
    for (var i = pool.Count - 1; i > 0; i--)
    {
        var j = Random.Shared.Next(i + 1);
        (pool[i], pool[j]) = (pool[j], pool[i]);
    }

    return pool.Take(Math.Min(count, pool.Count)).ToList();
}

// Walks up from the running executable's directory until it finds Drps.sln - the repo root
// marker every other file-path-resolving convention in this codebase (e.g. the shared
// watchlist config) relies on being a fixed, known location relative to the build output.
static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Drps.sln")))
            return dir.FullName;

        dir = dir.Parent;
    }

    throw new InvalidOperationException(
        $"Could not locate Drps.sln by walking up from {AppContext.BaseDirectory} - repo root not found.");
}

static IEnumerable<List<string>> Chunk(IReadOnlyList<string> source, int size)
{
    for (var i = 0; i < source.Count; i += size)
        yield return source.Skip(i).Take(size).ToList();
}
