using System.Text.Json;
using Drps.Ingestion.Feeders;
using Drps.Shared.Models;

namespace Drps.Tests.Feeders;

public class TiingoExDividendMapperTests
{
    // Real Tiingo /tiingo/daily/{ticker}/prices response shape, same fields TiingoFeeder's own
    // fixtures use. AAPL 2023-08-11's divCash=0.24 matches a real, independently-verified
    // ex-dividend amount (per CLAUDE.md's 2026-08-01 audit against stockanalysis.com).
    private const string MixedFixture = """
    [
        {"date": "2023-08-10T00:00:00.000Z", "open": 178.15, "high": 179.15, "low": 177.53, "close": 177.97, "volume": 54764400,
         "adjOpen": 175.9436, "adjHigh": 176.9314, "adjLow": 175.3315, "adjClose": 175.7614, "adjVolume": 54764400, "divCash": 0.0, "splitFactor": 1.0},
        {"date": "2023-08-11T00:00:00.000Z", "open": 177.32, "high": 178.62, "low": 176.55, "close": 177.79, "volume": 52036672,
         "adjOpen": 175.1211, "adjHigh": 176.4050, "adjLow": 174.3607, "adjClose": 175.5853, "adjVolume": 52036672, "divCash": 0.24, "splitFactor": 1.0},
        {"date": "2023-08-14T00:00:00.000Z", "open": 177.97, "high": 179.69, "low": 177.31, "close": 179.46, "volume": 43675600,
         "adjOpen": 175.7614, "adjHigh": 177.4614, "adjLow": 175.1112, "adjClose": 177.2331, "adjVolume": 43675600, "divCash": 0.0, "splitFactor": 1.0}
    ]
    """;

    [Fact]
    public void MapObservations_MixedDivCashValues_OnlySkipsZeroValueRows()
    {
        using var doc = JsonDocument.Parse(MixedFixture);

        var observations = TiingoExDividendMapper.MapObservations("AAPL", doc.RootElement);

        var observation = Assert.Single(observations);
        Assert.Equal(new DateOnly(2023, 8, 11), observation.ExDividendDate);
        Assert.Equal(0.24m, observation.Value);
    }

    [Fact]
    public void MapObservations_NonZeroDivCash_MapsValueProvenanceShapeCorrectly()
    {
        using var doc = JsonDocument.Parse(MixedFixture);

        var observation = Assert.Single(TiingoExDividendMapper.MapObservations("AAPL", doc.RootElement));

        Assert.Equal(SourceType.Tiingo, observation.Source);
        Assert.Equal("AAPL", observation.Symbol);
        Assert.Equal(1, observation.SampleCount);
        Assert.Null(observation.VariancePct);
        Assert.False(observation.Verified);
        Assert.NotEqual(Guid.Empty, observation.RequestId);
        Assert.True(observation.IngestedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void MapObservations_AllZeroDivCash_ReturnsEmpty()
    {
        const string allZeroFixture = """
        [
            {"date": "2026-07-09T00:00:00.000Z", "open": 210.50, "high": 212.30, "low": 209.80, "close": 211.90, "volume": 45123456,
             "adjOpen": 21.05, "adjHigh": 21.23, "adjLow": 20.98, "adjClose": 21.19, "adjVolume": 451234560, "divCash": 0.0, "splitFactor": 1.0}
        ]
        """;
        using var doc = JsonDocument.Parse(allZeroFixture);

        var observations = TiingoExDividendMapper.MapObservations("NVDA", doc.RootElement);

        Assert.Empty(observations);
    }

    [Fact]
    public void MapObservations_EmptyArray_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("[]");

        var observations = TiingoExDividendMapper.MapObservations("NVDA", doc.RootElement);

        Assert.Empty(observations);
    }

    [Fact]
    public void MapObservations_TwoNonZeroDivCashRows_MapsBothIndependently()
    {
        // JNJ 2023-08-25 ($1.19) and 2024-02-16 ($1.19) - real, independently-verified
        // ex-dividend dates/amounts from the same 2026-08-01 audit.
        const string twoDividendFixture = """
        [
            {"date": "2023-08-25T00:00:00.000Z", "open": 164.30, "high": 167.78, "low": 164.06, "close": 166.25, "volume": 18185517,
             "adjOpen": 151.6208, "adjHigh": 154.8322, "adjLow": 151.3993, "adjClose": 153.4203, "adjVolume": 18185517, "divCash": 1.19, "splitFactor": 1.0},
            {"date": "2024-02-16T00:00:00.000Z", "open": 156.60, "high": 157.255, "low": 155.67, "close": 156.55, "volume": 8540961,
             "adjOpen": 146.7694, "adjHigh": 147.3833, "adjLow": 145.8978, "adjClose": 146.7226, "adjVolume": 8540961, "divCash": 1.19, "splitFactor": 1.0}
        ]
        """;
        using var doc = JsonDocument.Parse(twoDividendFixture);

        var observations = TiingoExDividendMapper.MapObservations("JNJ", doc.RootElement);

        Assert.Equal(2, observations.Count);
        Assert.Equal(new DateOnly(2023, 8, 25), observations[0].ExDividendDate);
        Assert.Equal(1.19m, observations[0].Value);
        Assert.Equal(new DateOnly(2024, 2, 16), observations[1].ExDividendDate);
        Assert.Equal(1.19m, observations[1].Value);
        Assert.NotEqual(observations[0].RequestId, observations[1].RequestId);
    }
}
