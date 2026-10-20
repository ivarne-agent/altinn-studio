using System.Net;
using Altinn.Studio.EnvTopology;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Yarp.ReverseProxy.Forwarder;

namespace Altinn.Studio.StudioctlServer.Proxy;

/// <summary>
/// The localtest-v2 front door: every request on a platform port is matched against the bound topology
/// (the component prefixes studioctl wrote, plus /{org}/{app} for each discovered app) and forwarded to the
/// loopback port behind it. Nothing is buffered, so server-sent events stream, and an upstream gets five
/// minutes, which a PDF render or a large upload can need.
/// </summary>
internal static class PlatformProxy
{
    private const long MaxRequestBodyBytes = 200L * 1024 * 1024;

    private static readonly HttpMessageInvoker _client = new(
        new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(5),
        }
    );

    private static readonly ForwarderRequestConfig _requestConfig = new() { ActivityTimeout = TimeSpan.FromMinutes(5) };

    public static void ConfigureKestrel(IConfiguration configuration, KestrelServerOptions options)
    {
        var proxy = ProxyOptions.FromConfiguration(configuration);
        if (!proxy.Enabled)
            return;

        options.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
        foreach (var port in proxy.Ports)
            options.ListenAnyIP(port);
    }

    /// <summary>A request that arrived over TCP; the control API lives on the Unix socket, which has no address.</summary>
    public static bool IsProxyRequest(HttpContext context) => context.Connection.LocalIpAddress is not null;

    public static async Task Handle(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<ProxyOptions>();
        var index = context.RequestServices.GetRequiredService<BoundTopologyIndexAccessor>().Current;
        var path = context.Request.Path.Value ?? "/";

        var match =
            index.MatchRequest(context.Request.Host.Host, path) ?? index.MatchRequest(options.CanonicalHost, path);
        if (match?.HttpDestinationUri is not { } destination)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(
                $"No route for {context.Request.Host.Host}{path}. Is the localtest-v2 environment up (studioctl env status -r localtest-v2)?",
                context.RequestAborted
            );
            return;
        }

        if (!match.Route.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var forwarder = context.RequestServices.GetRequiredService<IHttpForwarder>();
        var transformer = context.RequestServices.GetRequiredService<PlatformTransformer>();
        var error = await forwarder.SendAsync(context, destination.ToString(), _client, _requestConfig, transformer);
        if (error == ForwarderError.None || context.Response.HasStarted)
            return;

        var feature = context.GetForwarderErrorFeature();
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(
            $"{match.Route.Component} at {destination} did not answer: {error} {feature?.Exception?.Message}",
            context.RequestAborted
        );
    }
}
