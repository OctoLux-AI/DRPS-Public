using Drps.Watchdog;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

// Drps.Watchdog - closes the gap CLAUDE.md's 8/12-8/13 outage exposed: Ingestion/Calculator/
// Gate crashed on startup for two consecutive nights with zero exception telemetry anywhere,
// and this went undetected for ~36 hours because nothing checked whether the expected
// WorkerRunRecords rows actually appeared. This is a one-shot check, meant to be run once a
// morning after the scheduled scan chain (01:45 launch -> 03:00/03:20/03:35 internal schedule)
// should have completed. No Task Scheduler entry is created by this project - that remains a
// manual step (per this task's own explicit instruction).

const string ConnectionString = @"Server=(localdb)\mssqllocaldb;Database=Drps;Trusted_Connection=True;";
string[] WorkerNames = ["Drps.Ingestion", "Drps.Calculator", "Drps.Gate"];

var expectedDate = ResolveExpectedTradingDate(DateOnly.FromDateTime(DateTime.Now));
Console.WriteLine($"[WATCHDOG]: checking scan chain completion for expected trading date {expectedDate:yyyy-MM-dd}");

Dictionary<string, WorkerRunRow?> rowsByWorker;
try
{
    rowsByWorker = await QueryWorkerRunRowsAsync(ConnectionString, WorkerNames, expectedDate, CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[WATCHDOG]: failed to query WorkerRunRecords - {ex.GetType().Name}: {ex.Message}");
    return 3;
}

var problems = new List<string>();
foreach (var workerName in WorkerNames)
{
    var row = rowsByWorker[workerName];
    if (row is null)
    {
        Console.WriteLine($"[WATCHDOG]: {workerName} - MISSING (no WorkerRunRecords row for {expectedDate:yyyy-MM-dd})");
        problems.Add(workerName);
    }
    else if (!row.AllSourcesSucceeded)
    {
        Console.WriteLine(
            $"[WATCHDOG]: {workerName} - PARTIAL FAILURE (row exists for {expectedDate:yyyy-MM-dd}, " +
            $"AllSourcesSucceeded=false, FailureDetail={row.FailureDetail ?? "(none)"})");
        problems.Add(workerName);
    }
    else
    {
        Console.WriteLine($"[WATCHDOG]: {workerName} - OK (completed {row.CompletedAt:yyyy-MM-dd HH:mm:ss})");
    }
}

if (problems.Count == 0)
{
    Console.WriteLine("[WATCHDOG]: all three workers completed successfully - no alert sent.");
    return 0;
}

// Only queried when there's something to report - the last RunDate each problem worker actually
// completed, regardless of AllSourcesSucceeded (row existence alone is "successfully completed,"
// per WorkerRunRecord's own doc comment - AllSourcesSucceeded is a finer-grained, separate signal
// about partial source failure within an otherwise-completed run).
Dictionary<string, DateOnly?> lastGoodRunByWorker;
try
{
    lastGoodRunByWorker = await QueryLastRunDatesAsync(ConnectionString, problems, CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[WATCHDOG]: failed to query last-known-good RunDate context - {ex.GetType().Name}: {ex.Message}");
    lastGoodRunByWorker = problems.ToDictionary(w => w, _ => (DateOnly?)null);
}

var messageLines = new List<string> { $"DRPS scan chain incomplete for {expectedDate:yyyy-MM-dd}:" };
foreach (var workerName in problems)
{
    var lastGood = lastGoodRunByWorker.GetValueOrDefault(workerName);
    var lastGoodText = lastGood.HasValue ? lastGood.Value.ToString("yyyy-MM-dd") : "never";
    messageLines.Add($"- {workerName}: last successful run {lastGoodText}");
}
var message = string.Join("\n", messageLines);

Console.WriteLine();
Console.WriteLine("[WATCHDOG]: alert message:");
Console.WriteLine(message);

var config = new ConfigurationBuilder()
    .AddJsonFile(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".APIKeys", "shared-APIKeys.json"),
        optional: true)
    .Build();

var appToken = config["Pushover:AppToken"];
var userKey = config["Pushover:UserKey"];

using var httpClient = new HttpClient();
var alertResult = await PushoverAlerter.SendAsync(httpClient, appToken, userKey, message, CancellationToken.None);

switch (alertResult.Outcome)
{
    case PushoverAlertOutcome.Sent:
        Console.WriteLine("[WATCHDOG]: Pushover alert sent.");
        return 1;
    case PushoverAlertOutcome.NotConfigured:
        Console.Error.WriteLine("[WATCHDOG]: Pushover:AppToken/Pushover:UserKey not found in shared secrets file - alert NOT sent.");
        return 2;
    default:
        Console.Error.WriteLine($"[WATCHDOG]: Pushover alert failed to send - {alertResult.Outcome}: {alertResult.Detail}");
        return 2;
}

// Weekday-only, same deliberate simplification TradingDayCalendar (Drps.Shared) already
// documents for this codebase - no market-holiday calendar. Duplicated here rather than
// referenced, per this project's zero-ProjectReference isolation invariant.
static DateOnly ResolveExpectedTradingDate(DateOnly today)
{
    var date = today;
    while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
    {
        date = date.AddDays(-1);
    }
    return date;
}

// The primary check (CLAUDE.md's own required, reportable query, per this task's explicit
// instruction) - one row expected per (WorkerName, RunDate) for each of the three workers.
static async Task<Dictionary<string, WorkerRunRow?>> QueryWorkerRunRowsAsync(
    string connectionString, string[] workerNames, DateOnly expectedDate, CancellationToken cancellationToken)
{
    const string sql = """
        SELECT WorkerName, RunDate, CompletedAt, AllSourcesSucceeded, FailureDetail
        FROM WorkerRunRecords
        WHERE WorkerName IN (@w1, @w2, @w3) AND RunDate = @expectedDate
        """;

    var result = workerNames.ToDictionary(w => w, _ => (WorkerRunRow?)null);

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    await using var command = new SqlCommand(sql, connection);
    command.Parameters.AddWithValue("@w1", workerNames[0]);
    command.Parameters.AddWithValue("@w2", workerNames[1]);
    command.Parameters.AddWithValue("@w3", workerNames[2]);
    command.Parameters.AddWithValue("@expectedDate", expectedDate.ToDateTime(TimeOnly.MinValue));

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        var workerName = reader.GetString(reader.GetOrdinal("WorkerName"));
        result[workerName] = new WorkerRunRow(
            CompletedAt: reader.GetDateTime(reader.GetOrdinal("CompletedAt")),
            AllSourcesSucceeded: reader.GetBoolean(reader.GetOrdinal("AllSourcesSucceeded")),
            FailureDetail: reader.IsDBNull(reader.GetOrdinal("FailureDetail")) ? null : reader.GetString(reader.GetOrdinal("FailureDetail")));
    }

    return result;
}

// Context-only query, run only for workers already flagged missing/failed above. Built with
// individually-named parameters (@p0, @p1, ...) rather than STRING_SPLIT - workerNames is always
// a small (1-3 item) subset of the fixed WorkerNames array, so a plain dynamic IN clause is both
// simpler and avoids any dependency on the database's compatibility level.
static async Task<Dictionary<string, DateOnly?>> QueryLastRunDatesAsync(
    string connectionString, List<string> workerNames, CancellationToken cancellationToken)
{
    var paramNames = workerNames.Select((_, i) => $"@p{i}").ToArray();
    var sql = $"""
        SELECT WorkerName, MAX(RunDate) AS LastRunDate
        FROM WorkerRunRecords
        WHERE WorkerName IN ({string.Join(", ", paramNames)})
        GROUP BY WorkerName
        """;

    var result = workerNames.ToDictionary(w => w, _ => (DateOnly?)null);

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    await using var command = new SqlCommand(sql, connection);
    for (var i = 0; i < workerNames.Count; i++)
    {
        command.Parameters.AddWithValue(paramNames[i], workerNames[i]);
    }

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        var workerName = reader.GetString(reader.GetOrdinal("WorkerName"));
        var lastRunDate = DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("LastRunDate")));
        result[workerName] = lastRunDate;
    }

    return result;
}

public sealed record WorkerRunRow(DateTime CompletedAt, bool AllSourcesSucceeded, string? FailureDetail);
