using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Shared.Exceptions;
using Microsoft.Extensions.Options;
using Polly.Retry;

namespace Drps.Execution.Alpaca;

// Real implementation of IAlpacaTradingClient, following AlpacaFeeder/AlpacaAccountFeeder's
// established shape: Polly-wrapped GETs, an explicit ConfigurationMissingException pre-flight
// check on blank credentials before any HTTP call, never throws on an expected failure mode
// (a real fetch failure returns Success = false; a confirmed "this doesn't exist" response
// is its own distinct outcome, per each method's own doc comment). Reuses the same
// Alpaca:KeyId/Alpaca:SecretKey credential pair AlpacaFeeder/AlpacaAccountFeeder already bind,
// via Drps.Ingestion's existing AlpacaOptions - no second, duplicate options class.
public class AlpacaTradingClient : IAlpacaTradingClient
{
    public const string HttpClientName = "AlpacaTradingClient";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AlpacaOptions _options;
    private readonly ILogger<AlpacaTradingClient> _logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    public AlpacaTradingClient(IHttpClientFactory httpClientFactory, IOptions<AlpacaOptions> options, ILogger<AlpacaTradingClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _retryPolicy = FeederRetryPolicy.Build("ALPACA-TRADING", logger);
    }

    public async Task<AlpacaAccountResult> GetAccountAsync(CancellationToken cancellationToken)
    {
        EnsureCredentialsPresent();

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var response = await _retryPolicy.ExecuteAsync(
                token => client.GetAsync("/v2/account", token), cancellationToken);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            var buyingPower = ParseRequiredDecimalString(doc.RootElement, "buying_power");
            var cash = ParseRequiredDecimalString(doc.RootElement, "cash");

            return new AlpacaAccountResult { Success = true, BuyingPower = buyingPower, Cash = cash };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ALPACA-TRADING-CLIENT]: GetAccountAsync failed");
            return new AlpacaAccountResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<AlpacaClockResult> GetClockAsync(CancellationToken cancellationToken)
    {
        EnsureCredentialsPresent();

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var response = await _retryPolicy.ExecuteAsync(
                token => client.GetAsync("/v2/clock", token), cancellationToken);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("is_open", out var isOpenElement)
                || isOpenElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException("Alpaca clock response missing or malformed 'is_open' field");

            var isOpen = isOpenElement.GetBoolean();
            var nextOpen = ParseRequiredDateTimeOffset(doc.RootElement, "next_open");
            var nextClose = ParseRequiredDateTimeOffset(doc.RootElement, "next_close");

            return new AlpacaClockResult { Success = true, IsOpen = isOpen, NextOpen = nextOpen, NextClose = nextClose };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ALPACA-TRADING-CLIENT]: GetClockAsync failed");
            return new AlpacaClockResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<AlpacaAssetResult> GetAssetAsync(string symbol, CancellationToken cancellationToken)
    {
        EnsureCredentialsPresent();

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var url = $"/v2/assets/{Uri.EscapeDataString(symbol)}";

            var response = await _retryPolicy.ExecuteAsync(
                token => client.GetAsync(url, token), cancellationToken);

            // A 404 here means Alpaca explicitly confirmed no tradable asset exists for this
            // symbol - not a real fetch failure, so it must not fold into the generic
            // Success = false path below. Same carve-out precedent as TiingoFeeder's 404
            // handling. Deliberately checked before EnsureSuccessStatusCode() and rethrown
            // past the catch-all further down so it propagates to the caller distinctly.
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new SymbolNotFoundException(symbol);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("symbol", out var symbolElement) || symbolElement.ValueKind != JsonValueKind.String)
                throw new JsonException("Alpaca asset response missing or malformed 'symbol' field");

            if (!doc.RootElement.TryGetProperty("tradable", out var tradableElement)
                || tradableElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException("Alpaca asset response missing or malformed 'tradable' field");

            if (!doc.RootElement.TryGetProperty("status", out var statusElement) || statusElement.ValueKind != JsonValueKind.String)
                throw new JsonException("Alpaca asset response missing or malformed 'status' field");

            return new AlpacaAssetResult
            {
                Success = true,
                Symbol = symbolElement.GetString(),
                Tradable = tradableElement.GetBoolean(),
                Status = statusElement.GetString()
            };
        }
        catch (SymbolNotFoundException)
        {
            // Must propagate to the caller uncaught - the catch-all below would otherwise
            // convert it into an indistinguishable Success = false result.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ALPACA-TRADING-CLIENT]: GetAssetAsync failed for {Symbol}", symbol);
            return new AlpacaAssetResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<AlpacaPositionsResult> GetOpenPositionsAsync(CancellationToken cancellationToken)
    {
        EnsureCredentialsPresent();

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var response = await _retryPolicy.ExecuteAsync(
                token => client.GetAsync("/v2/positions", token), cancellationToken);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            // A genuinely empty account returns 200 + "[]" - a real, valid outcome, not an
            // error. Anything other than an array at the root is contract drift, not a
            // legitimate variant.
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("Alpaca positions response was not a JSON array (unexpected response shape)");

            var positions = new List<AlpacaPosition>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                positions.Add(new AlpacaPosition
                {
                    Symbol = GetRequiredString(entry, "symbol"),
                    Quantity = ParseRequiredDecimalString(entry, "qty"),
                    AverageEntryPrice = ParseRequiredDecimalString(entry, "avg_entry_price"),
                    MarketValue = ParseRequiredDecimalString(entry, "market_value"),
                    Side = GetRequiredString(entry, "side")
                });
            }

            return new AlpacaPositionsResult { Success = true, Positions = positions };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ALPACA-TRADING-CLIENT]: GetOpenPositionsAsync failed");
            return new AlpacaPositionsResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<AlpacaOrdersResult> ListOrdersBySymbolAsync(string symbol, CancellationToken cancellationToken)
    {
        EnsureCredentialsPresent();

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            // symbols/status confirmed against Alpaca's own GET /v2/orders docs before writing
            // this - symbols is a comma-separated list (a single symbol is valid input to it),
            // status=closed server-side filters to the terminal set (filled/canceled/expired/
            // rejected) so this never fetches Alpaca's open-order set, which the future
            // phantom-position search has no use for.
            var url = $"/v2/orders?symbols={Uri.EscapeDataString(symbol)}&status=closed";

            var response = await _retryPolicy.ExecuteAsync(
                token => client.GetAsync(url, token), cancellationToken);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            // Same "must be an array, or it's contract drift" precedent as
            // GetOpenPositionsAsync - a symbol with no closed order history yet returns
            // 200 + "[]", a real, valid outcome.
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("Alpaca orders response was not a JSON array (unexpected response shape)");

            var orders = new List<AlpacaOrder>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                orders.Add(new AlpacaOrder
                {
                    OrderId = GetRequiredString(entry, "id"),
                    ClientOrderId = GetRequiredString(entry, "client_order_id"),
                    Symbol = GetRequiredString(entry, "symbol"),
                    Side = GetRequiredString(entry, "side"),
                    Status = GetRequiredString(entry, "status"),
                    FilledQuantity = ParseOptionalDecimalString(entry, "filled_qty"),
                    FilledAveragePrice = ParseOptionalDecimalString(entry, "filled_avg_price"),
                    FilledAt = ParseOptionalDateTimeOffset(entry, "filled_at")
                });
            }

            return new AlpacaOrdersResult { Success = true, Orders = orders };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ALPACA-TRADING-CLIENT]: ListOrdersBySymbolAsync failed for {Symbol}", symbol);
            return new AlpacaOrdersResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<AlpacaOrderResult> PlaceOrderAsync(AlpacaOrderRequest request, CancellationToken cancellationToken)
    {
        EnsureCredentialsPresent();

        // Deliberately NOT wrapped in FeederRetryPolicy, unlike every read-only method above.
        // Per CLAUDE.md's Execution Layer: Second Design Decision, a timeout or 5xx on an
        // order call is genuinely ambiguous - Alpaca may have already processed it before the
        // response was lost - so blindly retrying risks a duplicate fire. The future
        // fire-mechanism task owns the real classification (clean 4xx = terminal, timeout/5xx
        // = query status by client_order_id first, retry once only if it's genuinely absent);
        // this raw call makes exactly one attempt and reports what happened.
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            var payload = new Dictionary<string, object?>
            {
                ["symbol"] = request.Symbol,
                ["qty"] = request.Quantity.ToString(CultureInfo.InvariantCulture),
                ["side"] = request.Side,
                ["type"] = request.Type,
                ["time_in_force"] = request.TimeInForce,
                ["client_order_id"] = request.ClientOrderId
            };
            if (request.LimitPrice is not null)
                payload["limit_price"] = request.LimitPrice.Value.ToString(CultureInfo.InvariantCulture);

            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/v2/orders", content, cancellationToken);

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var message = TryGetErrorMessage(json) ?? $"Alpaca order request rejected with status {(int)response.StatusCode}";

                // Retry classification (CLAUDE.md's Execution Layer: Second Design Decision) -
                // a 5xx is the server's own failure, genuinely ambiguous about whether the
                // order was processed before the response was lost; a 4xx is a clean, definite
                // rejection (Alpaca validated and refused the request). OrderFiringService's
                // retry logic branches on this flag directly.
                var isAmbiguous = (int)response.StatusCode >= 500;
                return new AlpacaOrderResult { Success = false, ErrorMessage = message, IsAmbiguousFailure = isAmbiguous };
            }

            var order = ParseOrder(json);
            order.Success = true;
            return order;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ALPACA-TRADING-CLIENT]: PlaceOrderAsync failed for {ClientOrderId}", request.ClientOrderId);

            // An exception means no confirmed HTTP response was ever received at all -
            // inherently ambiguous, same category as a 5xx above.
            return new AlpacaOrderResult { Success = false, ErrorMessage = ex.Message, IsAmbiguousFailure = true };
        }
    }

    public async Task<AlpacaOrderResult> GetOrderByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken)
    {
        EnsureCredentialsPresent();

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var url = $"/v2/orders:by_client_order_id?client_order_id={Uri.EscapeDataString(clientOrderId)}";

            var response = await _retryPolicy.ExecuteAsync(
                token => client.GetAsync(url, token), cancellationToken);

            // A confirmed 404 is an expected branch for this call specifically (the future
            // fire mechanism's ambiguous-timeout retry logic checks "does an order with this
            // ID exist yet" as ordinary control flow, per CLAUDE.md's Execution Layer: Second
            // Design Decision) - returned as data, not thrown, unlike GetAssetAsync's
            // SymbolNotFoundException carve-out above.
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new AlpacaOrderResult { Success = false, NotFound = true };

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var order = ParseOrder(json);
            order.Success = true;
            return order;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ALPACA-TRADING-CLIENT]: GetOrderByClientOrderIdAsync failed for {ClientOrderId}", clientOrderId);
            return new AlpacaOrderResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<AlpacaQuoteResult> GetLatestQuoteAsync(string symbol, CancellationToken cancellationToken)
    {
        EnsureCredentialsPresent();

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            // Market-data endpoint, not the trading API - built as a full absolute URI
            // against AlpacaOptions.DataBaseUrl rather than relying on this client's
            // BaseAddress (fixed to the trading API's BaseUrl for every other method here).
            // Same "build the absolute URL per request" precedent FinnhubExDividendFeeder
            // already uses for a client with no fixed BaseAddress at all - applied here to
            // reach a second base from a client whose BaseAddress is already fixed elsewhere.
            // feed=iex matches AlpacaFeeder's own bar-fetching convention (the free-tier feed).
            var url = new Uri(new Uri(_options.DataBaseUrl), $"/v2/stocks/{Uri.EscapeDataString(symbol)}/quotes/latest?feed=iex");

            var response = await _retryPolicy.ExecuteAsync(
                token => client.GetAsync(url, token), cancellationToken);

            // Same confirmed-404 carve-out as GetAssetAsync - Alpaca has no quote for a
            // symbol it doesn't recognize.
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new SymbolNotFoundException(symbol);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("quote", out var quoteElement) || quoteElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Alpaca quote response missing or malformed 'quote' field");

            // Market-data fields are bare JSON numbers, not quoted strings - same convention
            // AlpacaFeeder already uses for OHLCV bars on this same data API, distinct from
            // the trading API's quoted-string dollar fields (GetAccountAsync/
            // GetOpenPositionsAsync above).
            var ask = GetRequiredDecimal(quoteElement, "ap");
            var bid = GetRequiredDecimal(quoteElement, "bp");

            return new AlpacaQuoteResult { Success = true, Ask = ask, Bid = bid };
        }
        catch (SymbolNotFoundException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ALPACA-TRADING-CLIENT]: GetLatestQuoteAsync failed for {Symbol}", symbol);
            return new AlpacaQuoteResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<AlpacaCancelOrderResult> CancelOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        EnsureCredentialsPresent();

        // Deliberately NOT wrapped in FeederRetryPolicy, same reasoning as PlaceOrderAsync - a
        // mutating call. No retry story is specified for this task's scope; this makes exactly
        // one attempt and reports what happened.
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var response = await client.DeleteAsync($"/v2/orders/{Uri.EscapeDataString(orderId)}", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var message = TryGetErrorMessage(body) ?? $"Alpaca cancel request rejected with status {(int)response.StatusCode}";
                return new AlpacaCancelOrderResult { Success = false, ErrorMessage = message };
            }

            return new AlpacaCancelOrderResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ALPACA-TRADING-CLIENT]: CancelOrderAsync failed for {OrderId}", orderId);
            return new AlpacaCancelOrderResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private void EnsureCredentialsPresent()
    {
        // Checked before any HTTP call is attempted, same precedent as AlpacaFeeder/
        // AlpacaAccountFeeder - a missing credential throws ConfigurationMissingException
        // distinctly rather than being converted into a generic Success = false result
        // indistinguishable from a real source-side rejection.
        if (string.IsNullOrWhiteSpace(_options.KeyId))
            throw new ConfigurationMissingException("Alpaca:KeyId");
        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ConfigurationMissingException("Alpaca:SecretKey");
    }

    private static AlpacaOrderResult ParseOrder(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);

        return new AlpacaOrderResult
        {
            OrderId = GetRequiredString(doc.RootElement, "id"),
            ClientOrderId = GetRequiredString(doc.RootElement, "client_order_id"),
            Status = GetRequiredString(doc.RootElement, "status"),
            FilledQuantity = ParseOptionalDecimalString(doc.RootElement, "filled_qty"),
            FilledAveragePrice = ParseOptionalDecimalString(doc.RootElement, "filled_avg_price")
        };
    }

    private static string? TryGetErrorMessage(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            // A rejected order can come back with a non-JSON body on some failure paths -
            // caller falls back to the status-code message in that case.
            return null;
        }
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            throw new JsonException($"Alpaca response missing or malformed '{propertyName}' field");

        return value.GetString()!;
    }

    // Bare-JSON-number variant of ParseRequiredDecimalString below, for the market-data API's
    // quote fields (ap/bp) - distinct from the trading API's quoted-string dollar fields every
    // other method here parses.
    private static decimal GetRequiredDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
            throw new JsonException($"Alpaca response missing or malformed '{propertyName}' field");

        return value.GetDecimal();
    }

    private static decimal ParseRequiredDecimalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || !decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            throw new JsonException($"Alpaca response missing or malformed '{propertyName}' field");

        return parsed;
    }

    private static decimal? ParseOptionalDecimalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static DateTimeOffset ParseRequiredDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || !value.TryGetDateTimeOffset(out var parsed))
            throw new JsonException($"Alpaca response missing or malformed '{propertyName}' field");

        return parsed;
    }

    // Optional counterpart to ParseRequiredDateTimeOffset above - an order's filled_at is
    // legitimately null/absent for a canceled/expired/rejected order that never filled, same
    // "missing means absent, not malformed" precedent as ParseOptionalDecimalString.
    private static DateTimeOffset? ParseOptionalDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return value.TryGetDateTimeOffset(out var parsed) ? parsed : null;
    }
}
