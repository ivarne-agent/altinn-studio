using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Altinn.Studio.StudioctlServer.Discovery;
using Altinn.Studio.StudioctlServer.Proxy;

namespace Altinn.Studio.StudioctlServer.Provisioning;

/// <summary>
/// Where the provisioner finds the platform: the public origin studioctl-server itself proxies, the same
/// <c>Localtest:Url</c> the storage probe uses. Provisioning is on only when the proxy is, which is when the
/// localtest-v2 runtime is up.
/// </summary>
internal sealed record AppProvisionerOptions(Uri? PlatformUri, bool Enabled)
{
    public static AppProvisionerOptions FromConfiguration(IConfiguration configuration)
    {
        var proxy = ProxyOptions.FromConfiguration(configuration);
        var platform = Uri.TryCreate(configuration["Localtest:Url"], UriKind.Absolute, out var uri) ? uri : null;
        return new AppProvisionerOptions(platform, proxy.Enabled && platform is not null);
    }
}

/// <summary>
/// Deploys a discovered app to the localtest-v2 platform the way Altinn Studio deploys one: the application
/// metadata and texts go to Storage, the authorization policy to the PDP, the test users to the Register, and
/// Access Management is told the Register changed. The old localtest pulled all of this from the running app
/// on every request; the production components want it pushed once, and the app is the source, so apps
/// started from an IDE are provisioned exactly like apps started by `studioctl run`.
/// </summary>
internal sealed class AppProvisioner
{
    public const string HttpClientName = "app-provisioner";

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppProvisionerOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AppProvisioner> _logger;
    private readonly Dictionary<string, Uri> _provisioned = new(StringComparer.OrdinalIgnoreCase);

    public AppProvisioner(
        AppProvisionerOptions options,
        IHttpClientFactory httpClientFactory,
        ILogger<AppProvisioner> logger
    )
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;

    /// <summary>
    /// Brings the platform in line with the discovered apps: a new app, or one that came back on another
    /// port, is provisioned; an app that is gone has its test users removed. Called from the registry's
    /// refresh, so one app at a time and never concurrently.
    /// </summary>
    public async Task Reconcile(IReadOnlyList<DiscoveredApp> apps, CancellationToken cancellationToken)
    {
        if (!Enabled)
            return;

        var current = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
            current.TryAdd(app.AppId, app.BaseUri.Value);

        foreach (var (appId, baseUri) in current)
        {
            if (_provisioned.TryGetValue(appId, out var known) && known == baseUri)
                continue;

            try
            {
                await Provision(appId, baseUri, cancellationToken);
                _provisioned[appId] = baseUri;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Provisioning {AppId} from {BaseUri} failed; retrying on the next refresh",
                    appId,
                    baseUri
                );
            }
        }

        foreach (var appId in _provisioned.Keys.Where(appId => !current.ContainsKey(appId)).ToArray())
        {
            try
            {
                await Deprovision(appId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Removing {AppId} from the platform failed", appId);
            }
            _provisioned.Remove(appId);
        }
    }

    private async Task Provision(string appId, Uri appBaseUri, CancellationToken cancellationToken)
    {
        var (org, app) = SplitAppId(appId);
        var platform = _options.PlatformUri ?? throw new InvalidOperationException("platform uri is required");
        using var client = _httpClientFactory.CreateClient(HttpClientName);

        var appRoot = new Uri(appBaseUri, $"/{org}/{app}/");
        var metadata =
            await GetBytes(client, new Uri(appRoot, "api/v1/applicationmetadata?checkOrgApp=false"), cancellationToken)
            ?? throw new InvalidOperationException("the app returned no application metadata");
        var policy = await GetString(client, new Uri(appRoot, "api/v1/meta/authorizationpolicy"), cancellationToken);
        var testData = await GetBytes(client, new Uri(appRoot, "testData.json"), cancellationToken);
        var texts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in await GetLanguages(client, appRoot, cancellationToken))
        {
            if (
                await GetBytes(
                    client,
                    new Uri(appRoot, $"api/v1/texts/{Uri.EscapeDataString(language)}"),
                    cancellationToken
                ) is
                { } text
            )
                texts[language] = text;
        }

        var studioToken = await GetStudioToken(client, platform, cancellationToken);

        // 1. Register: the app's test users as posted (an empty body: none).
        await Send(
            client,
            HttpMethod.Put,
            new Uri(platform, $"/register/api/localtest/apps/{org}/{app}"),
            Json(testData),
            null,
            cancellationToken
        );

        // 2. Storage: application metadata and texts through the production endpoints, created or replaced.
        await CreateOrReplace(
            client,
            new Uri(platform, $"/storage/api/v1/applications?appId={org}/{app}"),
            new Uri(platform, $"/storage/api/v1/applications/{org}/{app}"),
            () => Json(metadata),
            studioToken,
            cancellationToken
        );
        foreach (var (language, text) in texts)
        {
            await CreateOrReplace(
                client,
                new Uri(platform, $"/storage/api/v1/applications/{org}/{app}/texts"),
                new Uri(platform, $"/storage/api/v1/applications/{org}/{app}/texts/{Uri.EscapeDataString(language)}"),
                () => Json(text),
                studioToken,
                cancellationToken
            );
        }

        // 3. The PDP: the policy with Designer's placeholder replacement.
        if (!string.IsNullOrWhiteSpace(policy))
        {
            var resolved = policy
                .Replace("[ORG]", org, StringComparison.Ordinal)
                .Replace("[org]", org, StringComparison.Ordinal)
                .Replace("[APP]", app, StringComparison.Ordinal)
                .Replace("[app]", app, StringComparison.Ordinal);
            await Send(
                client,
                HttpMethod.Post,
                new Uri(
                    platform,
                    $"/authorization/api/v1/policies?org={Uri.EscapeDataString(org)}&app={Uri.EscapeDataString(app)}"
                ),
                new StringContent(resolved, Encoding.UTF8, "application/xml"),
                studioToken,
                cancellationToken
            );
        }

        // 4. Access Management keeps its own copy of parties, roles and resources.
        await Send(
            client,
            HttpMethod.Post,
            new Uri(platform, "/accessmanagement/api/localtest/refresh"),
            null,
            null,
            cancellationToken
        );

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Provisioned {AppId} from {BaseUri}: metadata, {TextCount} text resource(s), policy {HasPolicy}, test data {HasTestData}",
                appId,
                appBaseUri,
                texts.Count,
                policy is not null,
                testData is not null
            );
    }

    private async Task Deprovision(string appId, CancellationToken cancellationToken)
    {
        var (org, app) = SplitAppId(appId);
        var platform = _options.PlatformUri ?? throw new InvalidOperationException("platform uri is required");
        using var client = _httpClientFactory.CreateClient(HttpClientName);
        await Send(
            client,
            HttpMethod.Delete,
            new Uri(platform, $"/register/api/localtest/apps/{org}/{app}"),
            null,
            null,
            cancellationToken
        );
        await Send(
            client,
            HttpMethod.Post,
            new Uri(platform, "/accessmanagement/api/localtest/refresh"),
            null,
            null,
            cancellationToken
        );
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Removed {AppId}'s test users from the platform", appId);
    }

    private static async Task CreateOrReplace(
        HttpClient client,
        Uri create,
        Uri replace,
        Func<HttpContent> content,
        string token,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, create) { Content = content() };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return;

        await Send(client, HttpMethod.Put, replace, content(), token, cancellationToken);
    }

    private static async Task Send(
        HttpClient client,
        HttpMethod method,
        Uri uri,
        HttpContent? content,
        string? token,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"{method} {uri} returned {(int)response.StatusCode}: {Truncate(body)}");
        }
    }

    private static async Task<string> GetStudioToken(
        HttpClient client,
        Uri platform,
        CancellationToken cancellationToken
    )
    {
        var token =
            await GetString(client, new Uri(platform, "/Home/GetTestStudioToken"), cancellationToken)
            ?? throw new InvalidOperationException("the localtest app returned no Studio token");
        return token.Trim().Trim('"');
    }

    private static async Task<IReadOnlyList<string>> GetLanguages(
        HttpClient client,
        Uri appRoot,
        CancellationToken cancellationToken
    )
    {
        var body = await GetBytes(client, new Uri(appRoot, "api/v1/applicationlanguages"), cancellationToken);
        if (body is null)
            return [];
        var languages = JsonSerializer.Deserialize<List<ApplicationLanguage>>(body, _jsonOptions) ?? [];
        return [.. languages.Select(static l => l.Language).Where(static l => !string.IsNullOrWhiteSpace(l))];
    }

    private static async Task<byte[]?> GetBytes(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static async Task<string?> GetString(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        var bytes = await GetBytes(client, uri, cancellationToken);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    private static HttpContent Json(byte[]? body)
    {
        var content = new ByteArrayContent(body ?? []);
        if (body is not null)
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static (string Org, string App) SplitAppId(string appId)
    {
        var parts = appId.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            throw new ArgumentException($"invalid app id {appId}", nameof(appId));
        return (parts[0], parts[1]);
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300] + "...";

    private sealed record ApplicationLanguage(string Language);
}
