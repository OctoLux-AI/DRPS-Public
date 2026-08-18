using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Drps.Diagnostics;

/// <summary>
/// One row of TickerBot's /v2/series response for a single ticker - full OHLCV, already
/// parsed, timestamp normalized to a UTC-anchored DateOnly (matching RawOhlcvBar.Timestamp's
/// own daily-bar convention of UTC-midnight-truncated dates). Any of the five numeric fields
/// may be null if TickerBot's response omitted or null'd that specific column for that bar -
/// each field is compared independently below, so a missing Volume does not block comparing
/// Open/High/Low/Close for the same date.
/// </summary>
internal sealed record TickerBotBar(
    DateOnly Date,
    decimal? Open,
    decimal? High,
    decimal? Low,
    decimal? Close,
    long? Volume);

/// <summary>
/// One source's (Alpaca or Tiingo) raw OHLCV bar for a single date, loaded directly from
/// RawOhlcvBars - deduplicated to the most recently ingested row per date where more than one
/// exists (a realistic outcome of append-only re-ingestion across separate runs), matching
/// BarReconciliationService's own "most recent by IngestedAt/Id wins" precedent.
/// </summary>
internal sealed record ReferenceBar(decimal Open, decimal High, decimal Low, decimal Close, long Volume);

internal enum OhlcvField { Open, High, Low, Close, Volume }

internal sealed record FieldDiscrepancy(OhlcvField Field, decimal TickerBotValue, decimal ReferenceValue, decimal AbsoluteDiff, decimal PercentDiff);

/// <summary>
/// One audited ticker's full result - what Program.cs prints per CLAUDE.md's TickerBot OHLCV
/// audit task (2026-08-14): matching/mismatched date counts, per-mismatch field-level detail,
/// coverage gaps (TickerBot missing a date both Alpaca AND Tiingo have), and possible-quality-
/// issue dates (TickerBot has a date neither Alpaca nor Tiingo has). No pass/fail verdict is
/// computed here - the task is explicit that this reports raw findings only.
/// </summary>
public sealed record TickerAuditResult(
    string Ticker,
    string Category,
    DateOnly? WindowStart,
    DateOnly? WindowEnd,
    int AlpacaDateCount,
    int TiingoDateCount,
    int TickerBotDateCount,
    int MatchingDates,
    int MismatchedDates,
    IReadOnlyList<string> MismatchDetails,
    IReadOnlyList<DateOnly> CoverageGapDates,
    IReadOnlyList<DateOnly> PossibleQualityIssueDates,
    string? SkipReason);

public sealed record TickerBotVerificationSummary(IReadOnlyList<TickerAuditResult> TickerResults);

/// <summary>
/// One-time TickerBot-vs-DRPS OHLCV cross-verification audit (CLAUDE.md's "TickerBot
/// Verification Tool: Diagnostics Isolation Preserved Over Logic Reuse," 2026-08-06, extended
/// 2026-08-14 for a real three-way OHLCV accuracy audit against a mixed ticker sample).
///
/// Standalone by deliberate design, on two axes at once:
///   - Comparison logic: duplicates BarReconciliationService's (Drps.Ingestion/Orchestration/
///     BarReconciliationService.cs) hybrid tolerance check verbatim for Open/High/Low/Close
///     rather than referencing it - IsWithinOhlcTolerance below is a hand-copy, not a shared
///     helper. If BarReconciliationService's Tolerance/AbsoluteToleranceFloor constants ever
///     change, this copy must be updated by hand; there is no compile-time or runtime link
///     between the two. Volume has no such existing codebase precedent to copy - see
///     VolumeTolerance's own comment.
///   - Data access: reads the real "Drps" LocalDB via raw, parameterized
///     Microsoft.Data.SqlClient, never EF Core or any Drps.Shared DbContext -
///     Drps.Diagnostics carries zero ProjectReference to any other DRPS project, and this
///     class does not break that invariant. Read-only against every production table
///     (RawOhlcvBars, RollingDmaStates) - this run writes nothing to any database; the report
///     is the deliverable, per this task's own "report raw findings only" scope.
///
/// TickerBot's real API contract, confirmed directly against
/// https://tickerbot.io/docs/endpoints/series/get/ before this file was written, not guessed:
///   GET https://api.tickerbot.io/v2/series?tickers={symbol}&amp;columns=open,high,low,close,volume&amp;interval=1d&amp;limit=1000[&amp;cursor=...]
///   Authorization: Bearer &lt;key&gt;
///   200 response shape: { ..., "next_cursor": string|null,
///                          "series": { "TICKER": [ { "t": "yyyy-MM-dd", "open": number|null,
///                          "high": number|null, "low": number|null, "close": number|null,
///                          "volume": number|null }, ... ] } }
///   401 = missing/invalid key, 403 = valid key but capability not on plan.
///
/// The API key itself is never seen by this class at all - only an already-configured
/// HttpClient with the Authorization header already set is passed in (see Program.cs's
/// RunTickerBotVerificationAsync). No method below ever logs a header, a full request URI
/// with credentials embedded (there is none - the key lives in a header, never a query
/// string), or a raw HttpClient/HttpRequestMessage. Every catch block logs only
/// ex.GetType().Name/ex.Message, never ex.ToString() or anything that could echo request
/// internals.
/// </summary>
public sealed class TickerBotVerificationProbe
{
    private const string Resolution = "1Day";
    private const int PageLimit = 1000;

    // Raised from 3 to 5 (2026-08-14) - a real full-sample run lost 6/25 tickers to
    // exhausted-retry 429s at the old value, before Retry-After was honored (see
    // SendWithRetryAsync's own comment). Free-tier's 429 Retry-After can legitimately be up to
    // 60s; 5 attempts gives that room to actually resolve rather than giving up early.
    private const int MaxRetryAttempts = 5;

    // Duplicated standalone from BarReconciliationService, deliberately - see this class's
    // own doc comment and CLAUDE.md's 2026-08-06 decision.
    private const decimal Tolerance = 0.001m;
    private const decimal AbsoluteToleranceFloor = 0.02m;

    // Diagnostic-only, ad hoc default - unlike OHLC's tolerance above, this codebase has no
    // existing production Volume-comparison precedent to copy (CLAUDE.md itself notes
    // volume-ratio was tried and rejected as a reliable filter elsewhere, for a different
    // purpose). 1% relative is a reasonable diagnostic default for cross-vendor volume
    // reporting differences, not a locked or calibrated threshold - every Volume discrepancy
    // is still reported with its raw magnitude regardless of this cutoff.
    private const decimal VolumeTolerance = 0.01m;

    // SourceType (Drps.Shared.Models) ordinals, confirmed directly against that file, not
    // assumed - see SourceTypeName's own comment below for the full mapping.
    private const int AlpacaSourceOrdinal = 0;
    private const int TiingoSourceOrdinal = 4;

    private readonly HttpClient _httpClient;
    private readonly string _connectionString;
    private readonly DiagnosticLog _log;

    public TickerBotVerificationProbe(HttpClient httpClient, string connectionString, DiagnosticLog log)
    {
        _httpClient = httpClient;
        _connectionString = connectionString;
        _log = log;
    }

    /// <summary>
    /// Runs the full audit against a caller-supplied (Ticker, Category) sample. For each
    /// ticker: establishes a "last N trading days" window from Alpaca's own most recent N
    /// distinct dates (Alpaca is DRPS's primary/ground-truth source, so its own calendar is
    /// the trading-day reference, not a synthetic weekday count), loads Tiingo's bars within
    /// that same window, fetches TickerBot's full history and filters to the window, then
    /// compares every date present in more than one source.
    /// </summary>
    public async Task<TickerBotVerificationSummary> RunAsync(
        IReadOnlyList<(string Ticker, string Category)> tickers, int tradingDayWindow, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var results = new List<TickerAuditResult>();

        foreach (var (symbol, category) in tickers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyDictionary<DateOnly, ReferenceBar> alpacaBars;
            try
            {
                alpacaBars = await LoadMostRecentBarsAsync(connection, symbol, AlpacaSourceOrdinal, tradingDayWindow, cancellationToken);
            }
            catch (Exception ex)
            {
                _log.Warn($"{symbol}: failed to load local Alpaca bars - {ex.GetType().Name}: {ex.Message} - skipping symbol");
                results.Add(EmptyResult(symbol, category, $"Alpaca load failed: {ex.GetType().Name}: {ex.Message}"));
                continue;
            }

            if (alpacaBars.Count == 0)
            {
                _log.Warn($"{symbol}: no local Alpaca bars exist - cannot establish a comparison window - skipping symbol");
                results.Add(EmptyResult(symbol, category, "no local Alpaca bars found"));
                continue;
            }

            var windowStart = alpacaBars.Keys.Min();
            var windowEnd = alpacaBars.Keys.Max();

            IReadOnlyDictionary<DateOnly, ReferenceBar> tiingoBars;
            try
            {
                tiingoBars = await LoadBarsInRangeAsync(connection, symbol, TiingoSourceOrdinal, windowStart, windowEnd, cancellationToken);
            }
            catch (Exception ex)
            {
                _log.Warn($"{symbol}: failed to load local Tiingo bars - {ex.GetType().Name}: {ex.Message} - proceeding with Alpaca only");
                tiingoBars = new Dictionary<DateOnly, ReferenceBar>();
            }

            List<TickerBotBar> tickerBotAll;
            try
            {
                tickerBotAll = await FetchAllHistoryAsync(symbol, cancellationToken);
            }
            catch (Exception ex)
            {
                _log.Warn($"{symbol}: TickerBot fetch failed - {ex.GetType().Name}: {ex.Message} - skipping symbol");
                results.Add(EmptyResult(symbol, category, $"TickerBot fetch failed: {ex.GetType().Name}: {ex.Message}"));
                continue;
            }

            var tickerBotBars = tickerBotAll
                .Where(b => b.Date >= windowStart && b.Date <= windowEnd)
                .ToDictionary(b => b.Date);

            _log.Info(
                $"{symbol} ({category}): window {windowStart:yyyy-MM-dd}..{windowEnd:yyyy-MM-dd}, " +
                $"Alpaca={alpacaBars.Count} Tiingo={tiingoBars.Count} TickerBot={tickerBotBars.Count} date(s).");

            var result = CompareTicker(symbol, category, windowStart, windowEnd, alpacaBars, tiingoBars, tickerBotBars);
            results.Add(result);

            _log.Info(
                $"{symbol}: {result.MatchingDates} matching, {result.MismatchedDates} mismatched, " +
                $"{result.CoverageGapDates.Count} coverage gap(s), {result.PossibleQualityIssueDates.Count} possible quality issue(s).");
        }

        return new TickerBotVerificationSummary(results);
    }

    private static TickerAuditResult EmptyResult(string symbol, string category, string skipReason) =>
        new(symbol, category, null, null, 0, 0, 0, 0, 0, [], [], [], skipReason);

    private static TickerAuditResult CompareTicker(
        string symbol,
        string category,
        DateOnly windowStart,
        DateOnly windowEnd,
        IReadOnlyDictionary<DateOnly, ReferenceBar> alpacaBars,
        IReadOnlyDictionary<DateOnly, ReferenceBar> tiingoBars,
        IReadOnlyDictionary<DateOnly, TickerBotBar> tickerBotBars)
    {
        var unionDates = alpacaBars.Keys
            .Union(tiingoBars.Keys)
            .Union(tickerBotBars.Keys)
            .OrderBy(d => d)
            .ToList();

        var matching = 0;
        var mismatched = 0;
        var mismatchDetails = new List<string>();
        var coverageGaps = new List<DateOnly>();
        var possibleQualityIssues = new List<DateOnly>();

        foreach (var date in unionDates)
        {
            var hasAlpaca = alpacaBars.TryGetValue(date, out var alpacaBar);
            var hasTiingo = tiingoBars.TryGetValue(date, out var tiingoBar);
            var hasTickerBot = tickerBotBars.TryGetValue(date, out var tbBar);

            if (hasAlpaca && hasTiingo && !hasTickerBot)
            {
                // TickerBot missing a date both trusted sources have - a coverage gap.
                coverageGaps.Add(date);
                continue;
            }

            if (hasTickerBot && !hasAlpaca && !hasTiingo)
            {
                // TickerBot has a date neither trusted source has - a possible data quality
                // issue (fabricated/mis-dated bar), not merely a gap in the other direction.
                possibleQualityIssues.Add(date);
                continue;
            }

            if (!hasTickerBot || (!hasAlpaca && !hasTiingo))
            {
                // Not a comparable pair (e.g. only one of Alpaca/Tiingo has this date and
                // TickerBot doesn't, or some other single-source-only combination) - nothing
                // to diff, not a flagged condition either.
                continue;
            }

            var dateHasDiscrepancy = false;

            if (hasAlpaca)
            {
                var diffs = CompareFields(tbBar!, alpacaBar!);
                if (diffs.Count > 0)
                {
                    dateHasDiscrepancy = true;
                    foreach (var d in diffs)
                    {
                        mismatchDetails.Add(FormatDiscrepancy(date, "Alpaca", d));
                    }
                }
            }

            if (hasTiingo)
            {
                var diffs = CompareFields(tbBar!, tiingoBar!);
                if (diffs.Count > 0)
                {
                    dateHasDiscrepancy = true;
                    foreach (var d in diffs)
                    {
                        mismatchDetails.Add(FormatDiscrepancy(date, "Tiingo", d));
                    }
                }
            }

            if (dateHasDiscrepancy)
            {
                mismatched++;
            }
            else
            {
                matching++;
            }
        }

        return new TickerAuditResult(
            symbol, category, windowStart, windowEnd,
            alpacaBars.Count, tiingoBars.Count, tickerBotBars.Count,
            matching, mismatched, mismatchDetails, coverageGaps, possibleQualityIssues, null);
    }

    private static string FormatDiscrepancy(DateOnly date, string referenceSource, FieldDiscrepancy d) =>
        $"{date:yyyy-MM-dd} vs {referenceSource}: {d.Field} TickerBot={d.TickerBotValue} {referenceSource}={d.ReferenceValue} " +
        $"AbsDiff={d.AbsoluteDiff:F4} PctDiff={d.PercentDiff:P4}";

    private static List<FieldDiscrepancy> CompareFields(TickerBotBar tickerBot, ReferenceBar reference)
    {
        var diffs = new List<FieldDiscrepancy>();
        CompareOhlcField(OhlcvField.Open, tickerBot.Open, reference.Open, diffs);
        CompareOhlcField(OhlcvField.High, tickerBot.High, reference.High, diffs);
        CompareOhlcField(OhlcvField.Low, tickerBot.Low, reference.Low, diffs);
        CompareOhlcField(OhlcvField.Close, tickerBot.Close, reference.Close, diffs);
        CompareVolumeField(tickerBot.Volume, reference.Volume, diffs);
        return diffs;
    }

    // Same hybrid rule as BarReconciliationService.IsWithinTolerance - a field passes if it
    // satisfies the percentage tolerance OR the absolute floor, whichever is looser. Hand-
    // copied, not shared - see this class's own doc comment for why. A field entirely absent
    // from TickerBot's response for this bar (null) is not compared at all, not treated as a
    // mismatch - matches this codebase's existing "partial data source, no penalty" tiering.
    private static void CompareOhlcField(OhlcvField field, decimal? tickerBotValue, decimal referenceValue, List<FieldDiscrepancy> diffs)
    {
        if (tickerBotValue is null)
        {
            return;
        }

        var absoluteDiff = Math.Abs(referenceValue - tickerBotValue.Value);
        var withinTolerance = referenceValue == 0
            ? absoluteDiff <= AbsoluteToleranceFloor
            : absoluteDiff / referenceValue <= Tolerance || absoluteDiff <= AbsoluteToleranceFloor;

        if (!withinTolerance)
        {
            var percentDiff = referenceValue == 0 ? 0m : absoluteDiff / referenceValue;
            diffs.Add(new FieldDiscrepancy(field, tickerBotValue.Value, referenceValue, absoluteDiff, percentDiff));
        }
    }

    private static void CompareVolumeField(long? tickerBotVolume, long referenceVolume, List<FieldDiscrepancy> diffs)
    {
        if (tickerBotVolume is null)
        {
            return;
        }

        var absoluteDiff = Math.Abs(referenceVolume - tickerBotVolume.Value);
        var withinTolerance = referenceVolume == 0
            ? absoluteDiff == 0
            : (decimal)absoluteDiff / referenceVolume <= VolumeTolerance;

        if (!withinTolerance)
        {
            var percentDiff = referenceVolume == 0 ? 0m : (decimal)absoluteDiff / referenceVolume;
            diffs.Add(new FieldDiscrepancy(OhlcvField.Volume, tickerBotVolume.Value, referenceVolume, absoluteDiff, percentDiff));
        }
    }

    // Most recent N distinct dates for one (Symbol, Source), deduplicated to the most recently
    // ingested row per date (ROW_NUMBER over IngestedAt/Id descending) - handles the realistic
    // case of more than one RawOhlcvBar row for the same (Symbol, Source, Timestamp) from
    // append-only re-ingestion across separate runs, same "latest wins" precedent
    // BarReconciliationService itself already follows.
    private static async Task<IReadOnlyDictionary<DateOnly, ReferenceBar>> LoadMostRecentBarsAsync(
        SqlConnection connection, string symbol, int sourceOrdinal, int topN, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH RankedBars AS (
                SELECT [Timestamp], [Open], [High], [Low], [Close], [Volume],
                       ROW_NUMBER() OVER (PARTITION BY [Timestamp] ORDER BY IngestedAt DESC, Id DESC) AS rn
                FROM dbo.RawOhlcvBars
                WHERE Symbol = @Symbol AND Source = @Source AND Resolution = @Resolution
            )
            SELECT TOP (@TopN) [Timestamp], [Open], [High], [Low], [Close], [Volume]
            FROM RankedBars
            WHERE rn = 1
            ORDER BY [Timestamp] DESC
            """;

        var results = new Dictionary<DateOnly, ReferenceBar>();

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Symbol", symbol);
        command.Parameters.AddWithValue("@Source", sourceOrdinal);
        command.Parameters.AddWithValue("@Resolution", Resolution);
        command.Parameters.AddWithValue("@TopN", topN);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var date = DateOnly.FromDateTime(reader.GetDateTimeOffset(0).UtcDateTime.Date);
            results[date] = new ReferenceBar(
                reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4), reader.GetInt64(5));
        }

        return results;
    }

    // Same dedup rule as LoadMostRecentBarsAsync, but bounded to an explicit [start, end] date
    // window instead of "most recent N" - used for Tiingo, whose own coverage may be sparser
    // than Alpaca's within the window Alpaca's own dates already established.
    private static async Task<IReadOnlyDictionary<DateOnly, ReferenceBar>> LoadBarsInRangeAsync(
        SqlConnection connection, string symbol, int sourceOrdinal, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH RankedBars AS (
                SELECT [Timestamp], [Open], [High], [Low], [Close], [Volume],
                       ROW_NUMBER() OVER (PARTITION BY [Timestamp] ORDER BY IngestedAt DESC, Id DESC) AS rn
                FROM dbo.RawOhlcvBars
                WHERE Symbol = @Symbol AND Source = @Source AND Resolution = @Resolution
                    AND [Timestamp] >= @Start AND [Timestamp] <= @End
            )
            SELECT [Timestamp], [Open], [High], [Low], [Close], [Volume]
            FROM RankedBars
            WHERE rn = 1
            ORDER BY [Timestamp]
            """;

        var results = new Dictionary<DateOnly, ReferenceBar>();

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Symbol", symbol);
        command.Parameters.AddWithValue("@Source", sourceOrdinal);
        command.Parameters.AddWithValue("@Resolution", Resolution);
        command.Parameters.AddWithValue("@Start", start.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@End", end.ToDateTime(TimeOnly.MinValue).AddDays(1).AddTicks(-1));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var date = DateOnly.FromDateTime(reader.GetDateTimeOffset(0).UtcDateTime.Date);
            results[date] = new ReferenceBar(
                reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4), reader.GetInt64(5));
        }

        return results;
    }

    // One symbol's full paginated history - all five OHLCV columns, interval=1d, limit at the
    // documented max (1000), from/to both omitted so TickerBot returns genuinely "full
    // available history" rather than a bounded window (filtered locally to the comparison
    // window by the caller). Followed via next_cursor until null.
    private async Task<List<TickerBotBar>> FetchAllHistoryAsync(string symbol, CancellationToken cancellationToken)
    {
        var bars = new List<TickerBotBar>();
        string? cursor = null;

        do
        {
            var url = $"/v2/series?tickers={Uri.EscapeDataString(symbol)}&columns=open,high,low,close,volume&interval=1d&limit={PageLimit}";
            if (cursor is not null)
            {
                url += $"&cursor={Uri.EscapeDataString(cursor)}";
            }

            using var response = await SendWithRetryAsync(url, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException(
                    "TickerBot returned 401 Unauthorized - the configured key was rejected (key value never logged).");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException(
                    "TickerBot returned 403 Forbidden - valid key, but the series capability is not on the current plan.");
            }

            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("series", out var seriesElement)
                && seriesElement.TryGetProperty(symbol, out var rowsElement)
                && rowsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rowsElement.EnumerateArray())
                {
                    if (!row.TryGetProperty("t", out var timestampProperty) || timestampProperty.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var date = ParseDate(timestampProperty.GetString()!);
                    var open = ReadNullableDecimal(row, "open");
                    var high = ReadNullableDecimal(row, "high");
                    var low = ReadNullableDecimal(row, "low");
                    var close = ReadNullableDecimal(row, "close");
                    var volume = ReadNullableLong(row, "volume");

                    if (open is null && high is null && low is null && close is null && volume is null)
                    {
                        continue; // no usable value anywhere in this row - nothing to compare
                    }

                    bars.Add(new TickerBotBar(date, open, high, low, close, volume));
                }
            }

            cursor = root.TryGetProperty("next_cursor", out var cursorProperty) && cursorProperty.ValueKind == JsonValueKind.String
                ? cursorProperty.GetString()
                : null;
        }
        while (cursor is not null);

        return bars;
    }

    private static decimal? ReadNullableDecimal(JsonElement row, string propertyName) =>
        row.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetDecimal()
            : null;

    private static long? ReadNullableLong(JsonElement row, string propertyName) =>
        row.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt64()
            : null;

    // interval=1d rows carry a plain "yyyy-MM-dd" date string per TickerBot's own documented
    // example - parsed as DateOnly directly, matching RawOhlcvBar.Timestamp's own UTC-midnight
    // daily-bar convention (both sides of every comparison here are date-keyed, not time-of-day
    // keyed). Falls back to a full DateTimeOffset parse defensively, in case a future response
    // ever carries a full timestamp instead.
    private static DateOnly ParseDate(string t)
    {
        if (DateOnly.TryParse(t, CultureInfo.InvariantCulture, out var dateOnly))
        {
            return dateOnly;
        }

        var parsed = DateTimeOffset.Parse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        return DateOnly.FromDateTime(parsed.UtcDateTime.Date);
    }

    // Retry for transient failures (429 rate-limit, 5xx) only - 401/403 are terminal, real
    // answers from TickerBot and must not be retried. A fresh HttpRequestMessage per attempt
    // (an HttpResponseMessage/its request cannot be resent). Never logs the request - only the
    // outcome (status code), which never includes the Authorization header value.
    //
    // Backoff strategy confirmed directly against https://tickerbot.io/docs/errors/ before
    // being written, not guessed: a 429 carries a Retry-After header (seconds until the oldest
    // request in the rolling per-minute window ages out, max 60s) - honored exactly rather than
    // guessed at with a fixed delay, since the earlier full-sample run without this (2026-08-14)
    // lost 6 of 25 tickers to exhausted-retry 429s that a correctly-honored Retry-After would
    // have absorbed. 5xx has no such header - falls back to the docs' own recommended
    // exponential backoff (starts at 1s, doubles each attempt, capped at 60s).
    private async Task<HttpResponseMessage> SendWithRetryAsync(string url, CancellationToken cancellationToken)
    {
        var exponentialDelay = TimeSpan.FromSeconds(1);

        for (var attempt = 1; ; attempt++)
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);

            var isRateLimited = response.StatusCode == (HttpStatusCode)429;
            var isServerError = (int)response.StatusCode >= 500;
            if ((!isRateLimited && !isServerError) || attempt >= MaxRetryAttempts)
            {
                return response;
            }

            var delay = isRateLimited && response.Headers.RetryAfter?.Delta is { } retryAfterDelta
                ? retryAfterDelta
                : exponentialDelay;

            _log.Warn($"TickerBot request attempt {attempt}/{MaxRetryAttempts} returned {(int)response.StatusCode} - retrying in {delay.TotalSeconds:F0}s");
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
            exponentialDelay = TimeSpan.FromSeconds(Math.Min(exponentialDelay.TotalSeconds * 2, 60));
        }
    }
}
