using Altinn.App.Core.Extensions;
using Altinn.App.Core.Internal.Auth.AppToken;
using Altinn.App.Core.Internal.ProvisionedSecrets;
using Altinn.App.Tests.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

// The channel's type shares its name with the last segment of its namespace, so a test namespace mirroring the
// folder would hide it: C# finds the sibling namespace before the imported type.
namespace Altinn.App.Core.Tests.Internal;

/// <summary>
/// The app's token for Storage as a tenant of the provisioned secrets channel, and the Storage clients that
/// send it. The registrations under test are the ones every app gets.
/// </summary>
public sealed class AppTokenProvisioningTests
{
    /// <summary>
    /// The HTTP client names of the clients that call Storage. A typed client is named after its service type.
    /// </summary>
    public static TheoryData<string> StorageClients =>
        [
            "IApplicationClient",
            "IDataClient",
            "IInstanceClient",
            "IInstanceEventClient",
            "IProcessClient",
            "ISignClient",
        ];

    [Fact]
    public async Task Options_BindTheProvisionedFile()
    {
        using var tempDirectory = new TempDirectory();
        var expiresAt = new DateTimeOffset(2999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ProvisionedSecretsTestEnvironment.WriteAppToken(tempDirectory.Path, "provisioned-token", expiresAt);

        await using var serviceProvider = BuildAppProvider(tempDirectory.Path);

        var settings = serviceProvider.GetRequiredService<IOptions<AppTokenSettings>>().Value;
        Assert.Equal("provisioned-token", settings.Token);
        Assert.Equal(expiresAt, settings.ExpiresAt);
        Assert.Equal(expiresAt.AddHours(-1), settings.RefreshAt);
        Assert.Equal(expiresAt.AddHours(-2), settings.IssuedAt);
    }

    /// <summary>
    /// The operator rewrites the file before the token expires, and the next request carries the new token,
    /// without a restart.
    /// </summary>
    [Fact]
    public async Task Handler_SendsTheRewrittenToken()
    {
        using var tempDirectory = new TempDirectory();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(2);
        ProvisionedSecretsTestEnvironment.WriteAppToken(tempDirectory.Path, "token-before", expiresAt);

        await using var serviceProvider = BuildAppProvider(tempDirectory.Path);
        var handler = serviceProvider.GetRequiredService<AppTokenDelegatingHandler>();
        handler.InnerHandler = new CapturingHandler();
        using var invoker = new HttpMessageInvoker(handler);

        Assert.Equal("token-before", await SentToken(invoker));

        ProvisionedSecretsTestEnvironment.WriteAppToken(
            tempDirectory.Path,
            "token-after",
            expiresAt.AddHours(1),
            DateTime.UtcNow.AddMinutes(1)
        );

        string? sent = null;
        await Wait.Until(
            () =>
            {
                sent = SentToken(invoker).GetAwaiter().GetResult();
                return sent == "token-after";
            },
            TimeSpan.FromSeconds(30)
        );
    }

    [Theory]
    [MemberData(nameof(StorageClients))]
    public async Task StorageClient_SendsTheAppToken(string clientName)
    {
        using var tempDirectory = new TempDirectory();

        await using var serviceProvider = BuildAppProvider(tempDirectory.Path);
        var factory = serviceProvider.GetRequiredService<IHttpMessageHandlerFactory>();

        HttpMessageHandler? handler = factory.CreateHandler(clientName);
        var chain = new List<HttpMessageHandler>();
        while (handler is not null)
        {
            chain.Add(handler);
            handler = (handler as DelegatingHandler)?.InnerHandler;
        }

        Assert.Single(chain.OfType<AppTokenDelegatingHandler>());
    }

    private static async Task<string?> SentToken(HttpMessageInvoker invoker)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://storage/");
        await invoker.SendAsync(request, default);
        return request.Headers.TryGetValues(AppTokenDelegatingHandler.HeaderName, out var values)
            ? values.Single()
            : null;
    }

    /// <summary>
    /// The app's services as an app gets them, for secrets provisioned into <paramref name="secretsDirectory"/>.
    /// </summary>
    private static ServiceProvider BuildAppProvider(string secretsDirectory)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(ProvisionedSecretsTestEnvironment.VariablesFor(secretsDirectory))
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddPlatformServices(configuration, Mock.Of<IWebHostEnvironment>());
        services.AddAppServices(configuration, Mock.Of<IWebHostEnvironment>());

        // Not the strict provider: these are part of an app's registrations, and the rest supplies what some of
        // these services depend on.
        return services.BuildServiceProvider();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() => Path = Directory.CreateTempSubdirectory().FullName;

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
