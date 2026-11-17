using System.Collections.Concurrent;
using System.Diagnostics;
using Altinn.App.Api.Tests.Data;
using Altinn.App.Api.Tests.Fixtures.Instances;
using Altinn.App.Api.Tests.Fixtures.Platform;
using Altinn.App.Core.Configuration;
using Altinn.App.Core.Features;
using Altinn.App.Core.Features.Maskinporten.Models;
using Altinn.App.Core.Internal.Data;
using Altinn.App.Core.Internal.Instances;
using Altinn.App.Core.Internal.WorkflowEngine;
using Altinn.App.Core.Internal.WorkflowEngine.Http;
using Altinn.App.Core.Models;
using Altinn.App.Tests.Common.Mocks;
using Altinn.Platform.Storage.Interface.Models;
using App.IntegrationTests.Mocks.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace Altinn.App.Api.Tests.Fixtures;

/// <summary>
/// One started test host per test class, shared by its tests through <c>IClassFixture</c>. Building and starting
/// a host costs several times what a request costs, so the host outlives the tests and the per-test state is
/// reset between them instead: the mocks registered with <see cref="RegisterMock{T}"/>, <see cref="SendAsync"/>,
/// the <see cref="TelemetrySink"/>, the in-memory <see cref="Storage"/> and the fake workflow engine.
/// </summary>
/// <remarks>
/// Derive a sealed fixture per test class that names the app under test and makes its class-level registrations
/// in <see cref="ConfigureTestServices"/>. A test that needs a host configured differently from its class gets one
/// from <see cref="CreateClientWithOwnHost"/>.
/// </remarks>
public abstract class AppApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    protected abstract string Org { get; }
    protected abstract string App { get; }

    /// <summary>The platform behind the app, as selected by <see cref="TestPlatformSelection"/>.</summary>
    public IAppTestPlatform Platform { get; } = TestPlatformSelection.Platform;

    private readonly CurrentTestId _currentTestId = new();
    private readonly ConcurrentDictionary<Type, Mock> _mocks = new();
    private readonly List<WebApplicationFactory<Program>> _ownHosts = [];
    private readonly Dictionary<string, string?> _configOverrides = new();
    private readonly List<InstanceIdentifier> _createdInstances = [];
    private ITestOutputHelper? _output;

    /// <summary>
    /// The in-memory Storage API behind the app's own storage clients: seed instances, read them back or assert on
    /// <see cref="StorageClientInterceptor.RequestsResponses"/>. Emptied before every test.
    /// </summary>
    public StorageClientInterceptor Storage => Services.GetRequiredService<StorageClientInterceptor>();

    /// <summary>
    /// How a test authenticates to storage when it calls the clients itself, outside a request: a service owner
    /// token from the platform. Inside a request the clients use the request's own user.
    /// </summary>
    public StorageAuthenticationMethod StorageAuthentication =>
        StorageAuthenticationMethod.Custom(async () => JwtToken.Parse(await Platform.GetServiceOwnerToken()));

    /// <summary>The id of the test the host is serving, set by <see cref="BeginTest"/>.</summary>
    public TestId CurrentTestId =>
        _currentTestId.Value ?? throw new InvalidOperationException($"{nameof(BeginTest)} has not been called.");

    /// <summary>
    /// Answers every request the app's <see cref="HttpClient"/>s send while the platform is in memory. Set it in a
    /// test; <see cref="BeginTest"/> resets it to one that fails the test.
    /// </summary>
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> SendAsync { get; set; } = FailUnexpectedRequest;

    private static Task<HttpResponseMessage> FailUnexpectedRequest(HttpRequestMessage request) =>
        throw new NotImplementedException(
            $"Set {nameof(AppApiFixture)}.{nameof(SendAsync)} in the test when it makes the app use a real http client. "
                + $"Unexpected request: {request.Method} {request.RequestUri}"
        );

    /// <summary>
    /// Registers a mock for <typeparamref name="T"/> in the host. Its setups are reset before every test, and
    /// <see cref="ConfigureMocks"/> re-applies the baseline afterwards. Call it from <see cref="ConfigureTestServices"/>,
    /// which also runs for every host from <see cref="CreateOwnHost"/>: the fixture keeps one mock per type, so
    /// those hosts get the same instance and a setup made in a test reaches all of them.
    /// </summary>
    protected Mock<T> RegisterMock<T>(IServiceCollection services, MockBehavior behavior = MockBehavior.Strict)
        where T : class
    {
        var mock = (Mock<T>)_mocks.GetOrAdd(typeof(T), _ => new Mock<T>(behavior));
        services.Replace(ServiceDescriptor.Singleton(mock.Object));
        return mock;
    }

    /// <summary>Class-level service registrations, applied once when the host is built.</summary>
    protected virtual void ConfigureTestServices(IServiceCollection services) { }

    /// <summary>Baseline setups for the mocks from <see cref="RegisterMock{T}"/>, applied before every test.</summary>
    protected virtual void ConfigureMocks() { }

    /// <summary>
    /// The telemetry the host records. Override to listen to other sources or meters; the sink always filters on
    /// <see cref="CurrentTestId"/>, so a test only sees its own requests.
    /// </summary>
    protected virtual void ConfigureTelemetrySink(IServiceCollection services) =>
        services.AddTelemetrySink(
            additionalActivitySources: source => source.Name == "Microsoft.AspNetCore",
            testIdAccessor: () => _currentTestId.Value
        );

    /// <summary>
    /// Overrides an app setting for the host. Call it from <see cref="ConfigureTestServices"/> or earlier; the host
    /// reads the overrides when it is built.
    /// </summary>
    protected void OverrideAppSetting(string key, string? value) => _configOverrides[key] = value;

    /// <summary>
    /// Whether the test process's entry point is being invoked by a <see cref="WebApplicationFactory{TEntryPoint}"/>
    /// to build a host, as opposed to by the test runner to run the tests. The factory passes the host
    /// configuration as command line arguments, and it always sets the application name.
    /// </summary>
    public static bool IsWebApplicationFactoryCall(string[] args) =>
        args.Any(arg => arg.StartsWith("--applicationName=", StringComparison.OrdinalIgnoreCase));

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        ConfigureWebHost(builder, configureOwnHost: null);

    private void ConfigureWebHost(IWebHostBuilder builder, Action<IWebHostBuilder>? configureOwnHost)
    {
        string appRootPath = TestData.GetApplicationDirectory(Org, App);
        string appSettingsPath = Path.Join(appRootPath, "appsettings.json");

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(appSettingsPath)
            .AddInMemoryCollection(_configOverrides)
            .Build();
        IConfigurationSection appSettingSection = configuration.GetSection("AppSettings");

        builder.ConfigureLogging(logging => ConfigureFakeLogging(logging, () => _output));

        // The app files are read from the content root, which the test host takes from this setting
        builder.UseContentRoot(appRootPath);
        builder.ConfigureServices(services => services.Configure<AppSettings>(appSettingSection));
        builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton(_currentTestId)));
        builder.ConfigureTestServices(ConfigureTelemetrySink);
        if (Platform.Kind == TestPlatform.InMemory)
        {
            builder.ConfigureTestServices(ConfigureFakeHttpClientHandler);
            builder.ConfigureTestServices(ConfigureInProcessProcessEngineClient);
        }
        builder.ConfigureTestServices(ConfigureTestServices);
        if (configureOwnHost is not null)
        {
            configureOwnHost(builder);
        }
        // Last, so that the hooks every registration above added are applied to the loaded app files
        builder.ConfigureTestServices(AppFilesMutationHook.Apply);
    }

    public static void ConfigureFakeLogging(ILoggingBuilder builder, Func<ITestOutputHelper?>? output = null)
    {
        builder
            .ClearProviders()
            .AddFakeLogging(options =>
            {
                options.OutputSink = message =>
                {
                    try
                    {
                        output?.Invoke()?.WriteLine(message);
                    }
                    catch (InvalidOperationException)
                    {
                        // The test the output belonged to has finished; the host logs on, and nobody reads it.
                    }
                };
                if (output is null)
                {
                    options.FilteredLevels = new HashSet<LogLevel>
                    {
                        LogLevel.Warning,
                        LogLevel.Error,
                        LogLevel.Critical,
                    };
                }
                options.OutputFormatter = FakeLoggerXunit.OutputFormatter;
            });
    }

    /// <summary>
    /// Routes every <see cref="HttpClient"/> the app creates to <see cref="SendAsync"/>. The handler reads the
    /// delegate when a request is sent, because the client factory caches primary handlers across tests.
    /// </summary>
    private void ConfigureFakeHttpClientHandler(IServiceCollection services)
    {
        services.ConfigureHttpClientDefaults(c =>
            c.ConfigurePrimaryHttpMessageHandler(sp => new MockHttpMessageHandler(
                request => SendAsync(request),
                sp.GetRequiredService<ILogger<MockHttpMessageHandler>>()
            ))
        );
    }

    /// <summary>
    /// Replaces the HTTP-based workflow engine client with a synchronous in-process implementation that executes
    /// commands directly, and mocks the events client since it calls external services.
    /// </summary>
    private static void ConfigureInProcessProcessEngineClient(IServiceCollection services)
    {
        // Remove ALL registrations of IWorkflowEngineClient (AddHttpClient registers multiple descriptors)
        services.RemoveAll<IWorkflowEngineClient>();
        services.AddSingleton<IWorkflowEngineClient>(sp => new FakeWorkflowEngineClient(
            sp,
            sp.GetRequiredService<WorkflowCallbackStateService>()
        ));

        // The engine calls back in requests of their own, so a callback must not see the request that enqueued it.
        services.RemoveAll<IHttpContextAccessor>();
        services.AddSingleton<IHttpContextAccessor, WorkflowCallbackHttpContextAccessor>();

        var eventsClientMock = new Mock<Altinn.App.Core.Internal.Events.IEventsClient>();
        eventsClientMock
            .Setup(x =>
                x.AddEvent(
                    It.IsAny<string>(),
                    It.IsAny<Altinn.Platform.Storage.Interface.Models.Instance>(),
                    It.IsAny<Altinn.App.Core.Features.StorageAuthenticationMethod>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync("mock-event-id");
        services.RemoveAll<Altinn.App.Core.Internal.Events.IEventsClient>();
        services.AddSingleton(eventsClientMock.Object);
    }

    public virtual ValueTask InitializeAsync()
    {
        // Build and start the host once, here rather than in the first test, so that a startup failure is
        // reported as a fixture failure. The sink starts listening when it is constructed.
        _ = Services.GetRequiredService<TelemetrySink>();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Prepares the host for a test: a new <see cref="CurrentTestId"/>, the test's output for the host's logs,
    /// and every piece of per-test state reset.
    /// </summary>
    public void BeginTest()
    {
        if (_currentTestId.Value is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(BeginTest)} was called while another test was running. The tests of a class run one at a "
                    + "time; a fixture must not be shared by several classes."
            );
        }

        _output = TestContext.Current.TestOutputHelper;
        _currentTestId.Value = new TestId(Guid.NewGuid());
        SendAsync = FailUnexpectedRequest;

        foreach (var mock in _mocks.Values)
        {
            mock.Reset();
        }
        ConfigureMocks();

        Services.GetRequiredService<TelemetrySink>().Clear();
        Storage.Clear();
        (Services.GetService<IWorkflowEngineClient>() as FakeWorkflowEngineClient)?.Clear();
    }

    /// <summary>Deletes what the test created, then forgets the test.</summary>
    public async Task EndTestAsync()
    {
        try
        {
            await DeleteCreatedInstances();
        }
        finally
        {
            _currentTestId.Value = null;
            _output = null;
        }
    }

    /// <summary>
    /// Creates a new instance from <paramref name="template"/> through the host's <see cref="IInstanceClient"/> and
    /// <see cref="IDataClient"/>, the app's own clients talking to <see cref="Storage"/>, which assigns the instance
    /// guid and the data element ids. Returns the instance as stored. It is tracked and deleted by
    /// <see cref="DeleteCreatedInstances"/>.
    /// </summary>
    public async Task<Instance> CreateInstance(InstanceTemplate template, CancellationToken cancellationToken = default)
    {
        await using var scope = Services.CreateAsyncScope();
        var instanceClient = scope.ServiceProvider.GetRequiredService<IInstanceClient>();
        var dataClient = scope.ServiceProvider.GetRequiredService<IDataClient>();

        StorageAuthenticationMethod authentication = StorageAuthentication;
        Instance created = await instanceClient.CreateInstance(
            template.Org,
            template.App,
            template.ToInstance(),
            authentication,
            cancellationToken
        );
        var identifier = new InstanceIdentifier(created);
        lock (_createdInstances)
        {
            _createdInstances.Add(identifier);
        }

        foreach (InstanceTemplateDataElement element in template.DataElements)
        {
            if (element.Model is not null)
            {
                await dataClient.InsertFormData(
                    created,
                    element.DataType,
                    element.Model,
                    authentication,
                    cancellationToken
                );
            }
            else
            {
                using var content = new MemoryStream(element.Content ?? []);
                await dataClient.InsertBinaryData(
                    created.Id,
                    element.DataType,
                    element.ContentType
                        ?? throw new InvalidOperationException(
                            $"Data element {element.DataType} of the template has neither a model nor a content type."
                        ),
                    element.Filename,
                    content,
                    generatedFromTask: null,
                    authentication,
                    cancellationToken
                );
            }
        }

        return await instanceClient.GetInstance(
            template.App,
            template.Org,
            identifier.InstanceOwnerPartyId,
            identifier.InstanceGuid,
            authentication,
            cancellationToken
        );
    }

    /// <summary>
    /// Hard-deletes every instance <see cref="CreateInstance"/> made since the last call. A failure is written to
    /// the test output and does not fail the test.
    /// </summary>
    public async Task DeleteCreatedInstances()
    {
        InstanceIdentifier[] instances;
        lock (_createdInstances)
        {
            instances = [.. _createdInstances];
            _createdInstances.Clear();
        }
        if (instances.Length == 0)
        {
            return;
        }

        await using var scope = Services.CreateAsyncScope();
        var instanceClient = scope.ServiceProvider.GetRequiredService<IInstanceClient>();
        foreach (InstanceIdentifier instance in instances)
        {
            try
            {
                await instanceClient.DeleteInstance(
                    instance.InstanceOwnerPartyId,
                    instance.InstanceGuid,
                    hard: true,
                    StorageAuthentication
                );
            }
            catch (Exception e)
            {
                _output?.WriteLine($"Could not delete instance {instance} created by the test: {e}");
            }
        }
    }

    /// <summary>
    /// A client for the shared host. Works whether the host runs on the test server or listens on a port.
    /// </summary>
    public HttpClient CreateClient(bool includeTraceContext) =>
        includeTraceContext
            ? CreateDefaultClient(new DiagnosticHandler())
            : CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// The escape hatch: a client for a host of its own, configured like the shared one and then by
    /// <paramref name="configure"/>, for a test whose configuration differs from its class's. The host shares the
    /// fixture's <see cref="SendAsync"/>, test id and mocks, and is disposed with the fixture.
    /// </summary>
    public HttpClient CreateClientWithOwnHost(Action<IWebHostBuilder> configure, bool includeTraceContext = false)
    {
        var factory = CreateOwnHost(configure);
        return includeTraceContext
            ? factory.CreateDefaultClient(new DiagnosticHandler())
            : factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    /// <summary>
    /// The host behind <see cref="CreateClientWithOwnHost"/>, started, for a test that needs its services too.
    /// </summary>
    public WebApplicationFactory<Program> CreateOwnHost(Action<IWebHostBuilder> configure)
    {
        var factory = new OwnHostFactory(this, configure);
        lock (_ownHosts)
        {
            _ownHosts.Add(factory);
        }
        _ = factory.Services.GetRequiredService<TelemetrySink>();
        return factory;
    }

    /// <summary>
    /// A host built for one test. <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/> would run the
    /// extra configuration after the parent's, that is after the app files hooks were applied, so this configures
    /// the host in one pass instead.
    /// </summary>
    private sealed class OwnHostFactory(AppApiFixture fixture, Action<IWebHostBuilder> configure)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            fixture.ConfigureWebHost(builder, configure);
    }

    public override async ValueTask DisposeAsync()
    {
        WebApplicationFactory<Program>[] ownHosts;
        lock (_ownHosts)
        {
            ownHosts = [.. _ownHosts];
            _ownHosts.Clear();
        }
        foreach (var host in ownHosts)
        {
            await host.DisposeAsync();
        }
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private sealed class DiagnosticHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var activity = Activity.Current;
            var propagator = Propagators.DefaultTextMapPropagator;
            if (activity is not null)
            {
                propagator.Inject(
                    new PropagationContext(activity.Context, Baggage.Current),
                    request.Headers,
                    (c, k, v) => c.TryAddWithoutValidation(k, v)
                );
                Assert.Contains(request.Headers, h => h.Key == "traceparent"); // traceparent is mandatory in W3C
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
