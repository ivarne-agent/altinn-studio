namespace Altinn.Studio.StudioctlServer.Proxy;

/// <summary>
/// The platform proxy studioctl-server serves while the localtest-v2 runtime is up. <c>Proxy:Listen</c> is a
/// comma-separated list of host ports (empty: no proxy); studioctl passes it and reads it back from the status
/// endpoint. The ports are bound on every interface, because the components call the public origin back through
/// the container runtime's host gateway, which on Linux cannot reach a loopback-bound listener.
/// </summary>
internal sealed class ProxyOptions
{
    public const string ListenKey = "Proxy:Listen";
    public const string CanonicalHostKey = "Proxy:CanonicalHost";

    /// <summary>The configured value, verbatim.</summary>
    public string Listen { get; init; } = string.Empty;

    /// <summary>
    /// The host every route is registered on. A request addressed to another host (localhost from app-lib's
    /// PlatformSettings defaults, 127.0.0.1 from the storage probe) is matched as if it were this one.
    /// </summary>
    public string CanonicalHost { get; init; } = "local.altinn.cloud";

    public bool Enabled => Ports.Count > 0;

    public IReadOnlyList<int> Ports =>
        [
            .. Listen
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static value => int.TryParse(value, out var port) ? port : 0)
                .Where(static port => port > 0),
        ];

    public static ProxyOptions FromConfiguration(IConfiguration configuration)
    {
        return new ProxyOptions
        {
            Listen = configuration[ListenKey] ?? string.Empty,
            CanonicalHost = configuration[CanonicalHostKey] is { Length: > 0 } host ? host : "local.altinn.cloud",
        };
    }
}
