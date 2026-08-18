using System.Globalization;

namespace Drps.Diagnostics;

// Deliberately NOT reusing CboeRegimeFeeder/FredRegimeFeeder from Drps.Ingestion - same
// standalone-probe precedent already established by AlpacaBandwidthProbe (see this project's
// own .csproj comment): Drps.Diagnostics carries no ProjectReference to any other DRPS
// project, so nothing in production ever ends up calling into it, or vice versa. Parsing logic
// below mirrors those feeders' already-empirically-confirmed CSV formats (CLAUDE.md's "Regime
// Data Sourcing" decision, 2026-07-26) rather than re-deriving format assumptions from scratch
// - this tool exists to verify the numbers CLAUDE.md locked from that same data, not to
// re-litigate what the CSV shapes look like.
//
// Fetching and parsing are deliberately separate methods (not one combined fetch-and-parse
// call like the two production feeders use) - this task found .NET's HttpClient unreliable
// against fred.stlouisfed.org specifically (see CurlHttpFetcher's own doc comment) and needed
// to swap in curl.exe as the actual transport for FRED without touching the CSV-parsing logic
// at all. Cboe direct has shown no such issue in this tool's testing and still fetches via
// HttpClient.
public static class RegimeSeriesFetcher
{
    private const string CboeExpectedHeader = "DATE,OPEN,HIGH,LOW,CLOSE";

    // Full-history pull on every call, no query-string date-range parameter - matches
    // CboeRegimeFeeder's own documented behavior for this endpoint.
    public static async Task<IReadOnlyDictionary<DateOnly, decimal>> FetchCboeCloseSeriesAsync(
        HttpClient httpClient, string url, CancellationToken cancellationToken)
    {
        var csv = await httpClient.GetStringAsync(url, cancellationToken);
        return ParseCboeCsv(csv, url);
    }

    public static IReadOnlyDictionary<DateOnly, decimal> ParseCboeCsv(string csv, string sourceLabel)
    {
        var lines = csv.Split('\n');

        if (lines.Length == 0 || !lines[0].Trim().Equals(CboeExpectedHeader, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cboe response at {sourceLabel} did not start with the expected header '{CboeExpectedHeader}' - format may have changed.");
        }

        var result = new Dictionary<DateOnly, decimal>();
        var skipped = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;

            var fields = line.Split(',');
            if (fields.Length != 5)
            {
                skipped++;
                continue;
            }

            try
            {
                var date = DateOnly.ParseExact(fields[0].Trim(), "MM/dd/yyyy", CultureInfo.InvariantCulture);
                var close = decimal.Parse(fields[4], NumberStyles.Number, CultureInfo.InvariantCulture);
                result[date] = close;
            }
            catch (FormatException)
            {
                skipped++;
            }
        }

        if (skipped > 0)
            Console.WriteLine($"  [RegimeSeriesFetcher] Cboe {sourceLabel}: skipped {skipped} malformed/unparseable row(s).");

        return result;
    }

    // Holiday-row handling matches FredRegimeFeeder's own documented, empirically-confirmed
    // behavior: a calendar holiday still gets a dated row with nothing after the value column
    // (e.g. "2001-02-19,"), not FRED's "." missing-data placeholder - both are treated as
    // "no data for this date," never as a parse error and never stored as a zero/null close.
    public static async Task<IReadOnlyDictionary<DateOnly, decimal>> FetchFredCloseSeriesAsync(
        HttpClient httpClient, string url, string seriesId, CancellationToken cancellationToken)
    {
        var csv = await httpClient.GetStringAsync(url, cancellationToken);
        return ParseFredCsv(csv, seriesId);
    }

    public static IReadOnlyDictionary<DateOnly, decimal> ParseFredCsv(string csv, string seriesId)
    {
        var lines = csv.Split('\n');

        var expectedHeader = $"observation_date,{seriesId}";
        if (lines.Length == 0 || !lines[0].Trim().Equals(expectedHeader, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"FRED response for {seriesId} did not start with the expected header '{expectedHeader}' - format may have changed.");
        }

        var result = new Dictionary<DateOnly, decimal>();
        var holidayCount = 0;
        var malformedCount = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;

            var fields = line.Split(',');
            if (fields.Length != 2)
            {
                malformedCount++;
                continue;
            }

            var valueField = fields[1].Trim();
            if (string.IsNullOrWhiteSpace(valueField) || valueField == ".")
            {
                holidayCount++;
                continue;
            }

            try
            {
                var date = DateOnly.ParseExact(fields[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var close = decimal.Parse(valueField, NumberStyles.Number, CultureInfo.InvariantCulture);
                result[date] = close;
            }
            catch (FormatException)
            {
                malformedCount++;
            }
        }

        Console.WriteLine(
            $"  [RegimeSeriesFetcher] FRED {seriesId}: parsed {result.Count} observation(s), " +
            $"skipped {holidayCount} holiday row(s), {malformedCount} malformed row(s).");

        return result;
    }
}
