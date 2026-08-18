namespace Drps.Ingestion.Feeders;

// One Form 4 filing's identity within a ticker's submissions index - just enough to build
// the individual filing's archive URL. Deliberately not the parsed transaction data itself
// (see Form4PurchaseTransaction) - a single filing's XML can contain zero, one, or several
// individual transactions once actually fetched and parsed.
public readonly record struct Form4FilingReference(string AccessionNumber, string PrimaryDocument, DateOnly FilingDate);
