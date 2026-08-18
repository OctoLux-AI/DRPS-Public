namespace Drps.Ingestion.Feeders;

// Exists so FredRegimeFeeder's actual transport (curl.exe, see CurlFredCsvTransport's own doc
// comment for why) can be swapped for a fake in tests without needing an HttpMessageHandler-
// shaped fake - curl.exe isn't HTTP-client-shaped at all, so forcing this behind
// IHttpClientFactory/HttpMessageHandler the way every other feeder in this codebase does would
// mean pretending curl is something it isn't. One method, because FredRegimeFeeder only ever
// needs one thing from its transport: the raw CSV body for a given URL, or a thrown exception.
public interface IFredCsvTransport
{
    Task<string> FetchCsvAsync(string url, CancellationToken cancellationToken);
}
