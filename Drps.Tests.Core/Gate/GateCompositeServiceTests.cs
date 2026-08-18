using Drps.Gate.Scoring;
using Drps.Shared.Models;

namespace Drps.Tests.Gate;

// Most of this class's original coverage exercised GateCompositeService's composite-weighting
// formula and bucket-threshold logic directly - both are redacted for public release (see
// GateCompositeService.cs). What survives is the one test that never reaches that redacted
// logic at all: a rejected candidate is refused before scoring is ever attempted.
public class GateCompositeServiceTests
{
    private static readonly DateTime AsOf = new(2026, 7, 15, 12, 0, 0);

    // [REDACTED FOR PUBLIC RELEASE] Placeholder fixture values, not DRPS's real shipped
    // tuning - see README.md's "What's intentionally not public" section.
    private static readonly GateParameters TestParameters = new()
    {
        RsiLowerBound = 45m,
        RsiPeak = 55m,
        RsiUpperBound = 65m,
        RsiFloorQuality = 0.75m,
        RvolFloorMultiple = 1.2m,
        RvolCeilingMultiple = 2.8m,
        RvolFullWeight = 0.30m,
        RvolHalfWeight = 0.15m,
        RsiCompositeWeight = 0.70m,
        BuyThreshold = 0.85m,
        WatchThreshold = 0.75m,
        ExitThreshold = 0.70m,
        NoBuySessionCount = 2
    };

    private readonly GateCompositeService _service = new(TestParameters);

    [Fact]
    public void Score_RejectedResult_Throws()
    {
        var rejected = new GateQualityResult
        {
            RejectionReason = GateRejectionReason.DmaNotAligned,
            IsDmaAligned = false,
            IsDma5VerifiedAsyncResult = false
        };

        Assert.Throws<ArgumentException>(() => _service.Score(rejected, isCurrentlyHeld: false, AsOf));
    }
}
