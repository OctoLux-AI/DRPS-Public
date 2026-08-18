namespace Drps.Diagnostics;

// Deliberately not Microsoft.Extensions.Logging - this is a one-shot console tool with no
// hosting/DI container, not a hosted service, so a plain console+file writer is proportionate.
// Mirrors Drps.Ingestion's FileLoggerProvider in spirit (console output duplicated to a log
// file so a run can be reviewed after the fact) without pulling in the hosting machinery that
// pattern depends on.
public sealed class DiagnosticLog : IDisposable
{
    private readonly StreamWriter _writer;

    public DiagnosticLog(string logFilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
        _writer = new StreamWriter(logFilePath, append: false) { AutoFlush = true };
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Console.WriteLine(line);
        _writer.WriteLine(line);
    }

    public void Dispose() => _writer.Dispose();
}
