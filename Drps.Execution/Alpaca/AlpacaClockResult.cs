namespace Drps.Execution.Alpaca;

public class AlpacaClockResult
{
    public bool Success { get; set; }

    // All null whenever Success is false.
    public bool? IsOpen { get; set; }
    public DateTimeOffset? NextOpen { get; set; }
    public DateTimeOffset? NextClose { get; set; }

    public string? ErrorMessage { get; set; }
}
