using Drps.Calculator.Calendar;

namespace Drps.Tests.TestHelpers;

public class FakeTradingCalendarService : ITradingCalendarService
{
    private readonly Func<DateOnly, DateOnly, IReadOnlySet<DateOnly>>? _openDaysProvider;
    private readonly Exception? _throws;

    // Default: every calendar day in [start, end] is treated as open - convenient for tests
    // that aren't exercising gap-detection behavior itself.
    public FakeTradingCalendarService()
    {
        _openDaysProvider = (start, end) => AllDaysInRange(start, end);
    }

    public FakeTradingCalendarService(IReadOnlySet<DateOnly> openDays)
    {
        _openDaysProvider = (_, _) => openDays;
    }

    private FakeTradingCalendarService(Exception throwOnFetch)
    {
        _throws = throwOnFetch;
    }

    public static FakeTradingCalendarService ThatFails(Exception exception) => new(exception);

    public Task<IReadOnlySet<DateOnly>> GetOpenTradingDaysAsync(DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (_throws is not null)
        {
            throw _throws;
        }

        return Task.FromResult(_openDaysProvider!(start, end));
    }

    private static IReadOnlySet<DateOnly> AllDaysInRange(DateOnly start, DateOnly end)
    {
        var days = new HashSet<DateOnly>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            days.Add(d);
        }

        return days;
    }
}
