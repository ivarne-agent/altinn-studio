using Altinn.App.Core.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Altinn.App.Core.Features.Telemetry.AppToken;

namespace Altinn.App.Core.Internal.Auth.AppToken;

/// <summary>
/// <para>Answers which app token to send to Storage right now, if any. The token is read from the provisioned
/// file on every call, through <see cref="IOptionsMonitor{TOptions}.CurrentValue"/>, and never cached here: the
/// operator rewrites the file with an hour of a two-hour token left, and the next request after that carries
/// the new one.</para>
/// <para>A token that is missing or expired is not an error the app can do anything about, and Storage decides
/// what a request without one means. So this never throws: it reports the request through a counter, warns at
/// most once per <see cref="WarningInterval"/>, and answers that there is no token to send.</para>
/// <para>A singleton, because the warning's rate limit and the lifetime gauge must outlive the delegating
/// handlers, which the HTTP client factory recycles.</para>
/// </summary>
internal sealed class AppTokenProvider
{
    /// <summary>
    /// How far past <see cref="AppTokenSettings.ExpiresAt"/> the token is still sent. This covers a clock in
    /// the app's pod that runs ahead of the issuer's; Storage validates the token on its own clock regardless.
    /// </summary>
    internal static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The least time between two warnings about requests sent without a token. Every such request is
    /// counted; only the log is rate limited.
    /// </summary>
    internal static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(5);

    private readonly IOptionsMonitor<AppTokenSettings> _settings;
    private readonly ILogger<AppTokenProvider> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Telemetry? _telemetry;

    // The earliest timestamp, in TimeProvider ticks, at which the next warning may be logged.
    private long _nextWarningAt = long.MinValue;

    public AppTokenProvider(
        IOptionsMonitor<AppTokenSettings> settings,
        ILogger<AppTokenProvider> logger,
        TimeProvider? timeProvider = null,
        Telemetry? telemetry = null
    )
    {
        _settings = settings;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _telemetry = telemetry;

        _telemetry?.CreateAppTokenRemainingLifetimeGauge(RemainingLifetimeSeconds);
    }

    /// <summary>
    /// The token to send to Storage now, or null when there is no usable one.
    /// </summary>
    public string? GetToken()
    {
        AppTokenSettings settings = _settings.CurrentValue;
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (string.IsNullOrWhiteSpace(settings.Token))
        {
            ReportMissing(MissingTokenReason.Missing, settings, now);
            return null;
        }

        if (settings.ExpiresAt is not { } expiresAt || expiresAt + ClockSkew <= now)
        {
            ReportMissing(MissingTokenReason.Expired, settings, now);
            return null;
        }

        return settings.Token;
    }

    private double? RemainingLifetimeSeconds()
    {
        AppTokenSettings settings = _settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(settings.Token) || settings.ExpiresAt is not { } expiresAt)
        {
            return null;
        }

        return (expiresAt - _timeProvider.GetUtcNow()).TotalSeconds;
    }

    private void ReportMissing(MissingTokenReason reason, AppTokenSettings settings, DateTimeOffset now)
    {
        _telemetry?.RecordStorageRequestWithoutAppToken(reason);

        long nowTicks = now.UtcTicks;
        long nextWarningAt = Interlocked.Read(ref _nextWarningAt);
        if (nowTicks < nextWarningAt)
        {
            return;
        }

        // Only the caller that moves the window forward logs, so concurrent requests warn once between them.
        if (
            Interlocked.CompareExchange(ref _nextWarningAt, nowTicks + WarningInterval.Ticks, nextWarningAt)
            != nextWarningAt
        )
        {
            return;
        }

        if (reason == MissingTokenReason.Missing)
        {
            _logger.LogWarning(
                "Sending requests to Storage without an app token: the platform has provisioned none. "
                    + "Storage may reject them. This is logged at most once every {WarningInterval}.",
                WarningInterval
            );
        }
        else
        {
            _logger.LogWarning(
                "Sending requests to Storage without an app token: the provisioned token expired at {ExpiresAt} "
                    + "and has not been replaced. Storage may reject them. This is logged at most once every "
                    + "{WarningInterval}.",
                settings.ExpiresAt,
                WarningInterval
            );
        }
    }
}
