namespace Drps.Calculator.Logging;

/// <summary>
/// Writes log output to a rolling daily file (Logs/drps-calculator-yyyyMMdd.log),
/// in addition to whatever other providers (console) are already registered.
/// Log level filtering is handled upstream by the generic host's logging
/// pipeline using the same "Logging" config section as every other provider,
/// so this provider sees exactly the same Information/Warning/Error events
/// the console does. Own copy of Drps.Ingestion's FileLoggerProvider - Calculator
/// references Drps.Shared only, not Drps.Ingestion, per CLAUDE.md's project layout.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly object _writeLock = new();

    public FileLoggerProvider(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    internal void Write(string line)
    {
        var path = Path.Combine(_logDirectory, $"drps-calculator-{DateTime.Now:yyyyMMdd}.log");
        lock (_writeLock)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    public void Dispose()
    {
    }
}
