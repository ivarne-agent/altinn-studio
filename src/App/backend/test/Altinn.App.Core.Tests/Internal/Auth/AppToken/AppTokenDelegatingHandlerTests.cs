using Altinn.App.Core.Features;
using Altinn.App.Core.Internal.Auth.AppToken;
using Altinn.App.Tests.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Altinn.App.Core.Tests.Internal.Auth.AppToken;

public sealed class AppTokenDelegatingHandlerTests : IDisposable
{
    private static readonly DateTimeOffset _now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _timeProvider = new(_now);
    private readonly FakeLogger<AppTokenProvider> _logger = new();
    private readonly TelemetrySink _telemetry = new();
    private AppTokenSettings _settings = new();

    [Fact]
    public async Task SendsTheToken_WhenItIsValid()
    {
        _settings = Token("valid-token", expiresAt: _now.AddHours(1));

        var request = await Send();

        Assert.Equal(["valid-token"], request.Headers.GetValues(AppTokenDelegatingHandler.HeaderName));
        Assert.Empty(RequestsWithoutToken());
        Assert.Empty(_logger.Collector.GetSnapshot());
    }

    [Fact]
    public async Task SendsNoToken_AndCountsTheRequest_WhenNoneIsProvisioned()
    {
        var request = await Send();

        Assert.False(request.Headers.Contains(AppTokenDelegatingHandler.HeaderName));
        var measurement = Assert.Single(RequestsWithoutToken());
        Assert.Equal(1, measurement.Value);
        Assert.Equal("missing", measurement.Tags[Telemetry.InternalLabels.Reason]);
        var log = Assert.Single(_logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, log.Level);
    }

    [Fact]
    public async Task SendsNoToken_AndCountsTheRequest_WhenItHasExpired()
    {
        _settings = Token("expired-token", expiresAt: _now - AppTokenProvider.ClockSkew);

        var request = await Send();

        Assert.False(request.Headers.Contains(AppTokenDelegatingHandler.HeaderName));
        var measurement = Assert.Single(RequestsWithoutToken());
        Assert.Equal("expired", measurement.Tags[Telemetry.InternalLabels.Reason]);
    }

    [Fact]
    public async Task SendsNoToken_WhenItSaysNothingAboutWhenItExpires()
    {
        _settings = new AppTokenSettings { Token = "token-without-expiry" };

        var request = await Send();

        Assert.False(request.Headers.Contains(AppTokenDelegatingHandler.HeaderName));
        Assert.Equal("expired", Assert.Single(RequestsWithoutToken()).Tags[Telemetry.InternalLabels.Reason]);
    }

    /// <summary>
    /// The pod's clock may run ahead of the issuer's, so a token is sent until shortly after it expires.
    /// </summary>
    [Fact]
    public async Task SendsTheToken_WithinTheClockSkewAfterItExpires()
    {
        _settings = Token("just-expired-token", expiresAt: _now - AppTokenProvider.ClockSkew + TimeSpan.FromSeconds(1));

        var request = await Send();

        Assert.Equal(["just-expired-token"], request.Headers.GetValues(AppTokenDelegatingHandler.HeaderName));
    }

    /// <summary>
    /// The token is read on every request, never cached: whatever the file says now is what goes out.
    /// </summary>
    [Fact]
    public async Task SendsTheCurrentToken_OnEveryRequest()
    {
        var options = new Mock<IOptionsMonitor<AppTokenSettings>>();
        options
            .SetupSequence(o => o.CurrentValue)
            .Returns(Token("first-token", _now.AddHours(1)))
            .Returns(Token("second-token", _now.AddHours(2)));
        using var invoker = CreateInvoker(options.Object, out var inner);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://storage/"), default);
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://storage/"), default);

        Assert.Equal(["first-token"], inner.Requests[0].Headers.GetValues(AppTokenDelegatingHandler.HeaderName));
        Assert.Equal(["second-token"], inner.Requests[1].Headers.GetValues(AppTokenDelegatingHandler.HeaderName));
    }

    /// <summary>
    /// A request sent through the handler again, as a retry would, carries the token once.
    /// </summary>
    [Fact]
    public async Task ReplacesAnEarlierToken_OnTheSameRequest()
    {
        _settings = Token("valid-token", expiresAt: _now.AddHours(1));
        var request = new HttpRequestMessage(HttpMethod.Get, "http://storage/");
        request.Headers.TryAddWithoutValidation(AppTokenDelegatingHandler.HeaderName, "stale-token");

        var sent = await Send(request);

        Assert.Equal(["valid-token"], sent.Headers.GetValues(AppTokenDelegatingHandler.HeaderName));
    }

    /// <summary>
    /// Every request without a token is counted, but an app that has none would otherwise warn on every call to
    /// Storage.
    /// </summary>
    [Fact]
    public async Task Warns_AtMostOncePerInterval()
    {
        await Send();
        await Send();
        _timeProvider.Advance(AppTokenProvider.WarningInterval - TimeSpan.FromSeconds(1));
        await Send();

        Assert.Equal(3, RequestsWithoutToken().Count);
        Assert.Single(_logger.Collector.GetSnapshot());

        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        await Send();

        Assert.Equal(2, _logger.Collector.GetSnapshot().Count);
    }

    [Fact]
    public void PublishesTheRemainingLifetime()
    {
        _settings = Token("valid-token", expiresAt: _now.AddMinutes(90));
        _ = CreateProvider(OptionsFor(() => _settings));

        _telemetry.MeterListener.RecordObservableInstruments();

        var measurement = Assert.Single(Measurements(Telemetry.AppToken.MetricNameRemainingLifetime));
        Assert.Equal(TimeSpan.FromMinutes(90).TotalSeconds, measurement.Value);
    }

    [Fact]
    public void PublishesNoRemainingLifetime_WhenThereIsNoToken()
    {
        _ = CreateProvider(OptionsFor(() => _settings));

        _telemetry.MeterListener.RecordObservableInstruments();

        Assert.Empty(Measurements(Telemetry.AppToken.MetricNameRemainingLifetime));
    }

    private static AppTokenSettings Token(string token, DateTimeOffset expiresAt) =>
        new()
        {
            Token = token,
            IssuedAt = expiresAt - TimeSpan.FromHours(2),
            ExpiresAt = expiresAt,
            RefreshAt = expiresAt - TimeSpan.FromHours(1),
        };

    private async Task<HttpRequestMessage> Send(HttpRequestMessage? request = null)
    {
        using var invoker = CreateInvoker(OptionsFor(() => _settings), out var inner);
        await invoker.SendAsync(request ?? new HttpRequestMessage(HttpMethod.Get, "http://storage/"), default);
        return Assert.Single(inner.Requests);
    }

    // One provider for the whole test, as in the app, where it is a singleton and the handlers come and go.
    private AppTokenProvider? _provider;

    private HttpMessageInvoker CreateInvoker(IOptionsMonitor<AppTokenSettings> options, out CapturingHandler inner)
    {
        _provider ??= CreateProvider(options);
        inner = new CapturingHandler();
        return new HttpMessageInvoker(new AppTokenDelegatingHandler(_provider) { InnerHandler = inner });
    }

    private AppTokenProvider CreateProvider(IOptionsMonitor<AppTokenSettings> options) =>
        new(options, _logger, _timeProvider, _telemetry.Object);

    private static IOptionsMonitor<AppTokenSettings> OptionsFor(Func<AppTokenSettings> settings)
    {
        var options = new Mock<IOptionsMonitor<AppTokenSettings>>();
        options.Setup(o => o.CurrentValue).Returns(settings);
        return options.Object;
    }

    private IReadOnlyList<MetricMeasurement> RequestsWithoutToken() =>
        Measurements(Telemetry.AppToken.MetricNameRequestsWithoutToken);

    private IReadOnlyList<MetricMeasurement> Measurements(string name) =>
        _telemetry.CapturedMetrics.TryGetValue((name, _telemetry.Object.Meter.Name), out var measurements)
            ? measurements
            : [];

    public void Dispose() => _telemetry.Dispose();

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
