using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Altinn.App.Api.Tests.Fixtures.Instances;
using Altinn.App.Api.Tests.Utils;
using Altinn.App.Core.Constants;
using Altinn.Platform.Storage.Interface.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Altinn.App.Api.Tests.Fixtures;

/// <summary>
/// Base class for a test class that shares a host through <typeparamref name="TFixture"/>. xunit creates the
/// class once per test and runs the tests of a class one at a time, so the constructor and
/// <see cref="DisposeAsync"/> bracket each test with <see cref="AppApiFixture.BeginTest"/> and
/// <see cref="AppApiFixture.EndTestAsync"/>.
/// </summary>
public abstract class ApiTestBase<TFixture> : IClassFixture<TFixture>, IAsyncDisposable
    where TFixture : AppApiFixture
{
    internal static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    protected TFixture Fixture { get; }

    protected ITestOutputHelper Output { get; }

    /// <summary>The services of the shared host.</summary>
    protected IServiceProvider Services => Fixture.Services;

    protected ApiTestBase(TFixture fixture)
    {
        Fixture = fixture;
        Output =
            TestContext.Current.TestOutputHelper
            ?? throw new InvalidOperationException("No test is running; the test output is not available.");
        Fixture.BeginTest();
    }

    public virtual async ValueTask DisposeAsync()
    {
        await Fixture.EndTestAsync();
        GC.SuppressFinalize(this);
    }

    public async Task<HttpClient> GetUserClient(
        int userId = TestAuthentication.DefaultUserId,
        int partyId = TestAuthentication.DefaultUserPartyId,
        int authenticationLevel = TestAuthentication.DefaultUserAuthenticationLevel,
        bool includeTraceContext = false
    )
    {
        var client = Fixture.CreateClient(includeTraceContext);
        string token = await Fixture.Platform.GetUserToken(userId, partyId, authenticationLevel);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(AuthorizationSchemes.Bearer, token);
        return client;
    }

    public async Task<HttpClient> GetOrgClient(
        string orgNumber = TestAuthentication.DefaultOrgNumber,
        string scope = TestAuthentication.DefaultServiceOwnerScope,
        string serviceOwnerOrg = TestAuthentication.DefaultOrg,
        bool includeTraceContext = false
    )
    {
        var client = Fixture.CreateClient(includeTraceContext);
        string token = await Fixture.Platform.GetServiceOwnerToken(orgNumber, serviceOwnerOrg, scope);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(AuthorizationSchemes.Bearer, token);
        return client;
    }

    /// <summary>
    /// Creates a new instance from <paramref name="template"/> through the platform and returns it with the ids the
    /// platform assigned. It is deleted when the test ends.
    /// </summary>
    protected Task<Instance> CreateInstance(InstanceTemplate template) =>
        Fixture.CreateInstance(template, TestContext.Current.CancellationToken);

    /// <summary>Waits for the shared host to have recorded the given telemetry for this test, then snapshots it.</summary>
    protected async Task<TelemetrySnapshot> GetTelemetrySnapshot(int numberOfActivities, int numberOfMetrics)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var telemetry = Services.GetRequiredService<TelemetrySink>();
        var activitiesTask = telemetry.WaitForServerActivity(numberOfActivities, cts.Token);
        var metricsTask = telemetry.WaitForServerMetric(numberOfMetrics, cts.Token);
        await Task.WhenAll(activitiesTask, metricsTask);

        return telemetry.GetSnapshot();
    }

    /// <summary>
    /// Helper to quickly verify the status code and deserialize the content of a response.
    /// and print the content to output helper
    /// </summary>
    protected async Task<T> VerifyStatusAndDeserialize<T>(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode
    )
    {
        // Deserialize content and log everything if it fails
        var content = await response.Content.ReadAsStringAsync();
        Output.WriteLine($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}");
        Output.WriteLine(JsonUtils.IndentJson(content));
        // Verify status code
        response.Should().HaveStatusCode(expectedStatusCode);
        try
        {
            return JsonSerializer.Deserialize<T>(content, JsonSerializerOptions)
                ?? throw new JsonException("Content was \"null\"");
        }
        catch (Exception)
        {
            Output.WriteLine(string.Empty);
            Output.WriteLine(string.Empty);
            Output.WriteLine(
                $"Failed to deserialize content of {response.RequestMessage?.Method} request to {response.RequestMessage?.RequestUri} as {ReflectionUtils.GetTypeNameWithGenericArguments<T>()}:"
            );
            Output.WriteLine(string.Empty);
            throw;
        }
    }
}
