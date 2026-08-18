using System.Net;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Shared.Exceptions;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Feeders;

public class AlpacaAccountFeederTests
{
    // Real shape confirmed against Alpaca's own docs (docs.alpaca.markets) before writing
    // the feeder - buying_power is a JSON string, not a bare number, same as every other
    // numeric field on the account object.
    private const string AccountFixture = """
    {
        "id": "904837e3-3b76-47ec-b432-046db621571b",
        "account_number": "010203ABCD",
        "status": "ACTIVE",
        "currency": "USD",
        "buying_power": "262113.632",
        "cash": "-23140.2",
        "equity": "103820.56",
        "portfolio_value": "103820.56",
        "pattern_day_trader": false,
        "trading_blocked": false
    }
    """;

    private static AlpacaAccountFeeder CreateFeeder(FakeHttpMessageHandler handler, AlpacaOptions? options = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://paper-api.alpaca.markets") };
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        var resolvedOptions = Options.Create(options ?? new AlpacaOptions { KeyId = "test-key-id", SecretKey = "test-secret" });
        return new AlpacaAccountFeeder(httpClientFactory, resolvedOptions, NullLogger<AlpacaAccountFeeder>.Instance);
    }

    [Fact]
    public async Task FetchAccountAsync_SuccessfulResponse_ParsesBuyingPower()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AccountFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchAccountAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(262113.632m, result.BuyingPower);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task FetchAccountAsync_Always_RequestsAccountEndpoint()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AccountFixture) });
        var feeder = CreateFeeder(handler);

        await feeder.FetchAccountAsync(CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/v2/account", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task FetchAccountAsync_MissingBuyingPowerField_ReturnsFailureWithoutThrowing()
    {
        const string missingFieldFixture = """{"id": "abc", "currency": "USD"}""";
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(missingFieldFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchAccountAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.BuyingPower);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [Fact]
    public async Task FetchAccountAsync_BuyingPowerAsBareJsonNumber_ReturnsFailureWithoutThrowing()
    {
        // Alpaca's real contract always quotes numeric fields as strings - a bare JSON
        // number here is contract drift, not a legitimate variant, and must be treated as
        // a malformed response rather than silently coerced.
        const string bareNumberFixture = """{"buying_power": 262113.632}""";
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(bareNumberFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchAccountAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.BuyingPower);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [Fact]
    public async Task FetchAccountAsync_BuyingPowerIsNonNumericString_ReturnsFailureWithoutThrowing()
    {
        const string nonNumericFixture = """{"buying_power": "not-a-number"}""";
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(nonNumericFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchAccountAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.BuyingPower);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [Fact]
    public async Task FetchAccountAsync_MalformedNonJsonBody_ReturnsFailureWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json at all") });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchAccountAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.BuyingPower);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [Fact]
    public async Task FetchAccountAsync_NonRetryableClientError_ReturnsFailureWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"message":"forbidden"}""")
            });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchAccountAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.BuyingPower);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    // This test takes several real seconds to run: the retry policy's exponential backoff
    // (2s, 4s, 8s between attempts) actually elapses, same precedent as AlpacaFeeder's own
    // retry-exhaustion tests - no fake time provider exists yet to speed it up.
    [Fact]
    public async Task FetchAccountAsync_PersistentTransientFailure_RetriesThreeTimesThenReturnsFailure()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchAccountAsync(CancellationToken.None);

        Assert.Equal(4, callCount);
        Assert.False(result.Success);
        Assert.Null(result.BuyingPower);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [Fact]
    public async Task FetchAccountAsync_CallerCancellationWithoutTimeout_DoesNotRetry()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            throw new TaskCanceledException("The operation was canceled.");
        });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchAccountAsync(CancellationToken.None);

        Assert.Equal(1, callCount);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task FetchAccountAsync_MissingKeyId_ThrowsConfigurationMissingExceptionWithoutSendingRequest()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("HTTP call should never have been attempted"));
        var feeder = CreateFeeder(handler, new AlpacaOptions { KeyId = "", SecretKey = "test-secret" });

        var exception = await Assert.ThrowsAsync<ConfigurationMissingException>(() =>
            feeder.FetchAccountAsync(CancellationToken.None));

        Assert.Equal("Alpaca:KeyId", exception.ConfigKey);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task FetchAccountAsync_MissingSecretKey_ThrowsConfigurationMissingExceptionWithoutSendingRequest()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("HTTP call should never have been attempted"));
        var feeder = CreateFeeder(handler, new AlpacaOptions { KeyId = "test-key-id", SecretKey = "   " });

        var exception = await Assert.ThrowsAsync<ConfigurationMissingException>(() =>
            feeder.FetchAccountAsync(CancellationToken.None));

        Assert.Equal("Alpaca:SecretKey", exception.ConfigKey);
        Assert.Null(handler.LastRequest);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
