namespace Drps.Adjuster.Sizing;

/// <summary>
/// One signal's contribution to <see cref="MultiSignalMultiplierCombiner.Combine"/> - its own
/// already-computed, already-independently-capped raw multiplier, paired with the per-signal
/// weight that scales how much of its deviation from neutral (1.0) counts toward the combined
/// result.
/// </summary>
public readonly record struct WeightedMultiplier(decimal Weight, decimal RawMultiplier);

/// <summary>
/// Combines independent sizing-multiplier signals (insider, sentiment, and eventually regime)
/// into one CombinedMultiplier, replacing straight multiplicative stacking (Kelly x insider x
/// sentiment x ...). Multiplicative stacking made each earlier signal's own independently-tuned
/// cap mean less every time a new multiplicative factor was added - this class exists to fix
/// that without touching how any individual signal computes its own raw multiplier. This class
/// never computes a signal's raw multiplier itself - insider's and sentiment's own raw-
/// multiplier computations are completely unaffected by this class existing.
///
/// [REDACTED FOR PUBLIC RELEASE] The exact combination formula and clamp bounds are proprietary
/// and not included in this public repository - see README.md's "What's intentionally not
/// public" section.
/// </summary>
public static class MultiSignalMultiplierCombiner
{
    public const decimal ClampFloor = 0m;
    public const decimal ClampCeiling = 0m;

    public static decimal Combine(IReadOnlyList<WeightedMultiplier> signals)
    {
        // The trivial, non-proprietary case is left intact rather than redacted: when every
        // signal's own raw multiplier is already neutral (1.0, i.e. every deviation from
        // neutral is zero), the combined result is 1.0 under any reasonable combination
        // scheme - this doesn't depend on, and doesn't reveal, DRPS's actual weighting/clamp
        // formula. Any genuinely non-trivial input (at least one signal deviating from
        // neutral) exercises the real, redacted formula and is not reproduced here.
        if (signals.All(s => s.RawMultiplier == 1m))
        {
            return 1m;
        }

        throw new NotImplementedException("Redacted for public release - see README.md");
    }
}
