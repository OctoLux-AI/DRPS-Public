using System.Net;
using System.Text.Json;
using Drps.Shared.Exceptions;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace Drps.Calculator.Calendar;

/// <summary>
/// Hits Alpaca's /v2/calendar (Trading API) endpoint for the real set of open trading days
/// in a date range. Same credential/retry pattern as Drps.Ingestion's AlpacaFeeder
/// (ConfigurationMissingException pre-flight check, Polly retry on transient
/// HTTP failures/timeouts) - not shared code, since Calculator can't reference Ingestion,
/// but deliberately the same shape.
/// </summary>
public class AlpacaTradingCalendarService : ITradingCalendarService
{
    public const string HttpClientName = "AlpacaCalendarClient";
    private const int MaxRetries = 3;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AlpacaOptions _options;
    private readonly ILogger<AlpacaTradingCalendarService> _logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    public AlpacaTradingCalendarService(
        IHttpClientFactory httpClientFactory,
        IOptions<AlpacaOptions> options,
        ILogger<AlpacaTradingCalendarService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _retryPolicy = BuildRetryPolicy(logger);
    }

    public async Task<IReadOnlySet<DateOnly>> GetOpenTradingDaysAsync(DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        // Checked before any HTTP call is attempted, same as AlpacaFeeder - a missing
        // credential is a client-side configuration problem, not a real source rejection.
        if (string.IsNullOrWhiteSpace(_options.KeyId))
            throw new ConfigurationMissingException("Alpaca:KeyId");
        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ConfigurationMissingException("Alpaca:SecretKey");

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var url = $"/v2/calendar?start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}";

        var response = await _retryPolicy.ExecuteAsync(
            token => client.GetAsync(url, token), cancellationToken);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseOpenDays(json);
    }

    private static IReadOnlySet<DateOnly> ParseOpenDays(string responseJson)
    {
        var openDays = new HashSet<DateOnly>();

        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return openDays;

        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (entry.TryGetProperty("date", out var dateProp)
                && DateOnly.TryParse(dateProp.GetString(), out var date))
            {
                openDays.Add(date);
            }
        }

        return openDays;
    }

    // Same shape as Drps.Ingestion/Feeders/FeederRetryPolicy.cs - kept inline here rather
    // than extracted into a shared helper since this is currently the only Alpaca-calling
    // client in Drps.Calculator; extracting a one-caller abstraction would be premature.
    private static AsyncRetryPolicy<HttpResponseMessage> BuildRetryPolicy(ILogger logger)
    {
        return Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>(ex => ex.InnerException is TimeoutException)
            .OrResult(response => IsTransient(response.StatusCode))
            .WaitAndRetryAsync(
                retryCount: MaxRetries,
                sleepDurationProvider: (attempt, outcome, _) =>
                    outcome.Result?.Headers.RetryAfter?.Delta
                        ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetryAsync: (outcome, delay, attempt, _) =>
                {
                    logger.LogWarning(
                        "[ALPACA-CALENDAR/RETRY]: attempt {Attempt}/{MaxRetries} failed ({Reason}), retrying in {Delay}s",
                        attempt, MaxRetries,
                        outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString(),
                        delay.TotalSeconds);
                    return Task.CompletedTask;
                });
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests
        || statusCode == HttpStatusCode.RequestTimeout
        || (int)statusCode >= 500;
}
