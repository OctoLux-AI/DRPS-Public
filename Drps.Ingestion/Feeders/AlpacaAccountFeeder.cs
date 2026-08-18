using System.Globalization;
using System.Text.Json;
using Drps.Shared.Exceptions;
using Microsoft.Extensions.Options;
using Polly.Retry;

namespace Drps.Ingestion.Feeders;

/// <summary>
/// Reads Alpaca's own account state (buying power) - live, non-cacheable capital data for
/// Adjuster's future position-sizing/reserve-schedule logic, not historical/verifiable OHLCV
/// data. No persistence table and no cross-source verification here: Alpaca's own account is
/// the sole ground truth for "how much capital does *this* brokerage account have available" -
/// unlike OHLCV bars, there is no second source that could ever corroborate or dispute it.
///
/// Endpoint confirmed against Alpaca's own docs before writing this (docs.alpaca.markets):
/// GET /v2/account, same base URL as AlpacaOptions.BaseUrl (paper-api.alpaca.markets) already
/// names for future order-placement use, but this specific call is read-only - it returns the
/// account's current state and never places, modifies, or cancels an order. AlpacaFeeder's
/// existing scope note ("only reads bars, never touches Alpaca's trading API") describes
/// AlpacaFeeder specifically, not a blanket claim that no DRPS code may ever call anything
/// under that base URL - this feeder reads account state only; zero order-placement code
/// exists anywhere in DRPS as of this task.
///
/// Confirmed via Alpaca's docs: every numeric field in the account response (including
/// buying_power) is returned as a JSON string, not a bare JSON number - parsed explicitly via
/// decimal.Parse/InvariantCulture, same convention already used for other providers in this
/// codebase, never an implicit conversion.
/// </summary>
public class AlpacaAccountFeeder
{
    public const string HttpClientName = "AlpacaAccountClient";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AlpacaOptions _options;
    private readonly ILogger<AlpacaAccountFeeder> _logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    public AlpacaAccountFeeder(IHttpClientFactory httpClientFactory, IOptions<AlpacaOptions> options, ILogger<AlpacaAccountFeeder> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _retryPolicy = FeederRetryPolicy.Build("ALPACA-ACCOUNT", logger);
    }

    public async Task<AccountFetchResult> FetchAccountAsync(CancellationToken cancellationToken)
    {
        // Checked before any HTTP call is attempted, same precedent as AlpacaFeeder - a
        // missing credential throws ConfigurationMissingException distinctly rather than
        // being converted into a generic Success = false result indistinguishable from a
        // real source-side rejection. Reuses the same Alpaca:KeyId/SecretKey config
        // AlpacaFeeder already depends on - one credential pair, two different endpoints.
        if (string.IsNullOrWhiteSpace(_options.KeyId))
            throw new ConfigurationMissingException("Alpaca:KeyId");
        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ConfigurationMissingException("Alpaca:SecretKey");

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            var response = await _retryPolicy.ExecuteAsync(
                token => client.GetAsync("/v2/account", token), cancellationToken);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var buyingPower = ParseBuyingPower(json);

            return new AccountFetchResult { Success = true, BuyingPower = buyingPower };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ALPACA-ACCOUNT-FEEDER]: fetch failed");
            return new AccountFetchResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private static decimal ParseBuyingPower(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);

        if (!doc.RootElement.TryGetProperty("buying_power", out var buyingPowerElement)
            || buyingPowerElement.ValueKind != JsonValueKind.String
            || !decimal.TryParse(
                buyingPowerElement.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var buyingPower))
        {
            // Caught by FetchAccountAsync's own try/catch below, same as every other
            // feeder's JsonException-on-malformed-response handling - converted into a
            // Success = false result rather than propagating past this class.
            throw new JsonException("Alpaca account response missing or malformed 'buying_power' field");
        }

        return buyingPower;
    }
}
