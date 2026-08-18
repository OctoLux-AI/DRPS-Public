namespace Drps.Ingestion.Feeders;

/// <summary>
/// <see cref="MarketauxSentimentClient.GetSentimentAsync"/>'s output for one ticker.
/// <see cref="SentimentScore"/> is populated only when <see cref="Outcome"/> is
/// <see cref="MarketauxSentimentOutcome.RealValue"/>. <see cref="ArticlesFound"/>/
/// <see cref="ArticlesReturned"/> are Marketaux's own meta.found/meta.returned counts,
/// carried through purely as informational context for logging (e.g. distinguishing "zero
/// articles exist at all" from "articles exist but none mentioned this ticker by name") - they
/// never drive the Outcome decision itself.
/// </summary>
public sealed record MarketauxSentimentResult
{
    public required MarketauxSentimentOutcome Outcome { get; init; }
    public decimal? SentimentScore { get; init; }
    public int? ArticlesFound { get; init; }
    public int? ArticlesReturned { get; init; }
}
