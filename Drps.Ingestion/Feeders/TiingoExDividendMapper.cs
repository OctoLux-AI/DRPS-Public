using System.Text.Json;
using Drps.Shared.Models;

namespace Drps.Ingestion.Feeders;

// Deliberately separate from TiingoFeeder.MapBars, per CLAUDE.md's "Ex-Dividend Source:
// Tiingo Replaces Finnhub" decision (2026-08-01) — Option B (folding this straight into
// MapBars) was explicitly rejected there: OHLCV bar mapping has its own correction/
// re-verification lifecycle (the OHL-agreed/Close-resolved-to-Tiingo exception, the
// Tiingo-corrected-Close provenance flags), and RawOhlcvBar has no business carrying
// corporate-action data. This mapper reads the exact same already-parsed JsonElement
// TiingoFeeder.MapBars reads (no second HTTP call, no second parse), it just extracts a
// different field into a different entity.
public static class TiingoExDividendMapper
{
    public static IReadOnlyList<RawExDividendObservation> MapObservations(string symbol, JsonElement valuesElement)
    {
        var observations = new List<RawExDividendObservation>();
        var ingestedAt = DateTimeOffset.UtcNow;

        foreach (var entry in valuesElement.EnumerateArray())
        {
            // 0.0 means no dividend event on this bar's date — the overwhelming majority of
            // rows, per the 2026-08-01 audit (0.0 confirmed on every non-ex-div day tested).
            // Skipped entirely rather than written as a zero-value row: CLAUDE.md's own
            // Value+Provenance pattern models "no observation" as "no row," not a row whose
            // Value happens to be zero — writing one per non-dividend day would flood
            // RawExDividendObservations with noise for no informational gain.
            var divCash = entry.GetProperty("divCash").GetDecimal();
            if (divCash == 0m)
                continue;

            var exDividendDate = DateOnly.FromDateTime(entry.GetProperty("date").GetDateTimeOffset().UtcDateTime);

            observations.Add(new RawExDividendObservation
            {
                Source = SourceType.Tiingo,
                Symbol = symbol,
                ExDividendDate = exDividendDate,
                Value = divCash,
                SampleCount = 1,
                VariancePct = null,
                Verified = false,
                IngestedAt = ingestedAt,
                RequestId = Guid.NewGuid()
            });
        }

        return observations;
    }
}
