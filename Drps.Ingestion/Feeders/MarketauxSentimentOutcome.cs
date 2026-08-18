namespace Drps.Ingestion.Feeders;

/// <summary>
/// Every outcome <see cref="MarketauxSentimentClient.GetSentimentAsync"/> can return.
/// Per the Sentiment Adjuster Multiplier task's own spec, the client-level contract is three
/// outcomes (real value / no usable data / call failed) - <see cref="NoMatchingEntity"/> and
/// <see cref="NullSentimentScore"/> are both "no usable data" and currently resolve to the
/// same neutral multiplier downstream, but are kept as two distinct enum members specifically
/// so a caller's log line can tell "Marketaux never mentioned this ticker" apart from
/// "Marketaux mentioned it but scored it null" - different failure classes worth being able to
/// distinguish later, even though today's numeric outcome is identical for both.
/// </summary>
public enum MarketauxSentimentOutcome
{
    /// <summary>A matching entity was found with a non-null sentiment_score.</summary>
    RealValue,

    /// <summary>No entity anywhere in the response matched the requested ticker.</summary>
    NoMatchingEntity,

    /// <summary>A matching entity was found, but its sentiment_score was null.</summary>
    NullSentimentScore,

    /// <summary>The call itself failed - non-2xx, timeout, network error, or an unparseable/unexpected response shape.</summary>
    CallFailed
}
