using System.ComponentModel.DataAnnotations;
using System.Diagnostics.Metrics;
using NetEscapades.EnumGenerators;
using static Altinn.App.Core.Features.Telemetry.AppToken;
using Tag = System.Collections.Generic.KeyValuePair<string, object?>;

namespace Altinn.App.Core.Features;

partial class Telemetry
{
    private void InitAppToken(InitContext context)
    {
        InitMetricCounter(
            context,
            MetricNameRequestsWithoutToken,
            init: static m =>
            {
                foreach (var reason in MissingTokenReasonExtensions.GetValues())
                {
                    m.Add(0, new Tag(InternalLabels.Reason, reason.ToStringFast(useMetadataAttributes: true)));
                }
            }
        );
    }

    /// <summary>
    /// Counts one request to Storage that went out without an app token.
    /// </summary>
    internal void RecordStorageRequestWithoutAppToken(MissingTokenReason reason) =>
        _counters[MetricNameRequestsWithoutToken]
            .Add(1, new Tag(InternalLabels.Reason, reason.ToStringFast(useMetadataAttributes: true)));

    /// <summary>
    /// Publishes the app token's remaining lifetime in seconds, read through <paramref name="observe"/> each
    /// time the metric is collected. Negative once the token has expired, and absent while there is none.
    /// Called once, by the singleton that reads the token, because the value lives in the provisioned file
    /// rather than in anything this class holds.
    /// </summary>
    internal ObservableGauge<double> CreateAppTokenRemainingLifetimeGauge(Func<double?> observe) =>
        Meter.CreateObservableGauge(
            MetricNameRemainingLifetime,
            () => observe() is { } seconds ? [new Measurement<double>(seconds)] : Array.Empty<Measurement<double>>(),
            unit: "s",
            description: null
        );

    internal static class AppToken
    {
        internal static readonly string MetricNameRequestsWithoutToken = Metrics.CreateLibName(
            "app_token_storage_requests_without_token"
        );
        internal static readonly string MetricNameRemainingLifetime = Metrics.CreateLibName(
            "app_token_remaining_lifetime"
        );

        [EnumExtensions(MetadataSource = MetadataSource.DisplayAttribute)]
        internal enum MissingTokenReason
        {
            /// <summary>
            /// No token has been provisioned: the file is absent, or carries no token.
            /// </summary>
            [Display(Name = "missing")]
            Missing,

            /// <summary>
            /// The provisioned token has expired, or says nothing about when it expires.
            /// </summary>
            [Display(Name = "expired")]
            Expired,
        }
    }
}
