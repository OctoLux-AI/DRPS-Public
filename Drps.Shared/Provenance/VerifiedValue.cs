namespace Drps.Shared.Provenance;

public readonly record struct VerifiedValue<T>(
    T Value,
    string Source,
    DateTime FetchedAt,
    int SampleCount,
    decimal? VariancePct,
    bool Verified
);
