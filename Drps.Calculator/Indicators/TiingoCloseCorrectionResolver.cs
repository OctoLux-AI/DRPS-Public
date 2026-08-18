using Drps.Calculator.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Calculator.Indicators;

/// <summary>
/// Shared, multi-symbol batch lookup for the narrow, evidence-scoped Tiingo-Close-correction
/// exception (CLAUDE.md, "Reconciliation: Narrow Tiingo-Close Exception for the OHL-Agrees/
/// C-Disagrees Signature", 2026-07-17). DmaComputationService/RsiComputationService/
/// RvolComputationService/AtrComputationService each already resolve this same correction via
/// their own private, per-symbol GetTiingoCorrectedClosesAsync method - those are deliberately
/// left untouched here (already scrutinized, single-symbol-per-call by design). This class is
/// a second, batch-shaped implementation of the identical lookup (same Discrepancy ->
/// BarVerification.PrimarySourceValue join, same ResolutionMethod filter) for
/// RollingDmaStateService, which processes many tickers per run and needs one query across a
/// whole ticker batch rather than one query per symbol - the same batching discipline
/// RollingDmaStateService already applies to its own bar/state queries. NOT a general
/// Alpaca-vs-Tiingo preference: only dates where BarReconciliationService's own narrow
/// OHL-agreed/Close-disagreed signature actually fired are substituted.
/// </summary>
public static class TiingoCloseCorrectionResolver
{
    private const string Resolution = "1Day";

    public static async Task<Dictionary<(string Symbol, DateOnly Date), decimal>> GetCorrectedClosesAsync(
        CalculatorDbContext dbContext, IReadOnlyCollection<string> symbols, CancellationToken cancellationToken)
    {
        if (symbols.Count == 0)
        {
            return new Dictionary<(string, DateOnly), decimal>();
        }

        var correctedTimestamps = await dbContext.Discrepancies
            .Where(d => symbols.Contains(d.Symbol) && d.ResolutionMethod == DiscrepancyResolutionMethod.OhlAgreedCloseResolvedToTiingo)
            .Select(d => new { d.Symbol, d.Timestamp })
            .Distinct()
            .ToListAsync(cancellationToken);

        if (correctedTimestamps.Count == 0)
        {
            return new Dictionary<(string, DateOnly), decimal>();
        }

        var timestampsBySymbol = correctedTimestamps
            .GroupBy(c => c.Symbol)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Timestamp).ToHashSet());

        var verifications = await dbContext.BarVerifications
            .Where(v => symbols.Contains(v.Symbol) && v.Resolution == Resolution && v.PrimarySourceValue.HasValue)
            .Select(v => new { v.Symbol, v.Timestamp, v.PrimarySourceValue })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<(string Symbol, DateOnly Date), decimal>();
        foreach (var verification in verifications)
        {
            if (timestampsBySymbol.TryGetValue(verification.Symbol, out var timestamps)
                && timestamps.Contains(verification.Timestamp))
            {
                result[(verification.Symbol, DateOnly.FromDateTime(verification.Timestamp.UtcDateTime))] = verification.PrimarySourceValue!.Value;
            }
        }

        return result;
    }
}
