namespace Drps.Execution.PreFire;

// Input to PreFireGateService.EvaluateCloseAsync - an order-CLOSE attempt only. Deliberately
// has no ProposedDollarAmount field, unlike PreFireOpenRequest: the cash-floor check that field
// feeds is a buy-side-only concern (see EvaluateCloseAsync's own doc comment for why it is
// skipped entirely for closes, not merely fed a zero amount).
public class PreFireCloseRequest
{
    public required string Symbol { get; init; }
}
