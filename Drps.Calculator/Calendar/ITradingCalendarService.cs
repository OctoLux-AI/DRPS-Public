namespace Drps.Calculator.Calendar;

public interface ITradingCalendarService
{
    /// <summary>
    /// Returns every date in [start, end] (inclusive) that the market was actually open,
    /// per the real trading calendar - weekends/holidays are never included.
    /// </summary>
    Task<IReadOnlySet<DateOnly>> GetOpenTradingDaysAsync(DateOnly start, DateOnly end, CancellationToken cancellationToken);
}
