namespace Drps.Shared.Models;

public readonly record struct OhlcvBar(
    string Ticker,
    DateOnly BarDate,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    string Source,
    DateTime FetchedAt,
    int SampleCount,
    decimal? VariancePct,
    bool Verified
);
