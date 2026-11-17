using System.Collections.Concurrent;
using System.Net;
using Altinn.App.Core.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Altinn.App.Api.Tests.Fixtures;

/// <summary>
/// Proves the shared-host pattern itself: the tests of this class share one host, and the per-test state is
/// reset between them. The tests must hold whichever order they run in, so each records what it saw in a static
/// and checks the records of the tests before it.
/// </summary>
public sealed class SharedHostFixtureTests : ApiTestBase<SharedHostFixtureTests.SharedHostFixture>
{
    public interface IResettableService
    {
        string Greet(string name);
    }

    public sealed class SharedHostFixture : AppApiFixture
    {
        protected override string Org => "tdd";
        protected override string App => "contributer-restriction";

        public Mock<IResettableService> ResettableService { get; private set; } = null!;

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            ResettableService = RegisterMock<IResettableService>(services);
        }
    }

    private static readonly ConcurrentBag<IServiceProvider> _hostsSeen = [];
    private static readonly ConcurrentBag<Guid> _testIdsSeen = [];

    public SharedHostFixtureTests(SharedHostFixture fixture)
        : base(fixture) { }

    [Fact]
    public async Task First_test_shares_the_host_and_sees_only_its_own_state() => await RunSharedHostChecks();

    [Fact]
    public async Task Second_test_shares_the_host_and_sees_only_its_own_state() => await RunSharedHostChecks();

    private async Task RunSharedHostChecks()
    {
        // One host per class: every test resolves the same service provider
        _hostsSeen.Add(Services);
        _hostsSeen.Distinct().Should().ContainSingle();

        // A test id of its own per test
        Guid testId = Fixture.CurrentTestId.Value;
        _testIdsSeen.Should().NotContain(testId);
        _testIdsSeen.Add(testId);

        // A strict mock registered at class level has no setups left from the previous test
        var service = Services.GetRequiredService<IResettableService>();
        service.Invoking(s => s.Greet("anyone")).Should().Throw<MockException>();
        Fixture.ResettableService.Setup(s => s.Greet("tester")).Returns("hello tester");
        service.Greet("tester").Should().Be("hello tester");

        // A request to the shared host succeeds with a user token
        using var client = await GetUserClient();
        using var response = await client.GetAsync(
            "/tdd/contributer-restriction/api/v1/applicationmetadata",
            TestContext.Current.CancellationToken
        );
        var metadata = await VerifyStatusAndDeserialize<ApplicationMetadata>(response, HttpStatusCode.OK);
        metadata.Id.Should().Be("tdd/contributer-restriction");

        // The telemetry of the shared host only holds this test's request, tagged with this test's id
        var telemetry = Services.GetRequiredService<TelemetrySink>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        await telemetry.WaitForServerActivity(1, cts.Token);
        var snapshot = telemetry.GetSnapshot();
        var serverActivities = snapshot
            .Activities.Should()
            .NotBeNull()
            .And.Subject!.Where(a => a.Kind == System.Diagnostics.ActivityKind.Server)
            .ToList();
        serverActivities.Should().ContainSingle();
        serverActivities[0].Tags.Should().Contain(new KeyValuePair<string, object?>(nameof(TestId), testId));
    }

    [Fact]
    public async Task Escape_hatch_builds_a_host_of_its_own()
    {
        var ownHost = Fixture.CreateOwnHost(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton(new OwnHostMarker()))
        );

        ownHost.Services.Should().NotBeSameAs(Services);
        ownHost.Services.GetService<OwnHostMarker>().Should().NotBeNull();
        Services.GetService<OwnHostMarker>().Should().BeNull();

        using var client = ownHost.CreateClient();
        string token = await Fixture.Platform.GetUserToken();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var response = await client.GetAsync(
            "/tdd/contributer-restriction/api/v1/applicationmetadata",
            TestContext.Current.CancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class OwnHostMarker;
}
