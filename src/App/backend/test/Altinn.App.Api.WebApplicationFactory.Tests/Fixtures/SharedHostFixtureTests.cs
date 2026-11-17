using System.Collections.Concurrent;
using System.Net;
using Altinn.App.Api.Models;
using Altinn.App.Api.Tests.Data.apps.tdd.contributer_restriction.models;
using Altinn.App.Api.Tests.Fixtures.Instances;
using Altinn.App.Core.Internal.Data;
using Altinn.App.Core.Internal.Instances;
using Altinn.App.Core.Models;
using Altinn.Platform.Storage.Interface.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Altinn.App.Api.Tests.Fixtures;

/// <summary>
/// Proves the shared-host pattern itself: the tests of this class share one host, the per-test state is reset
/// between them, and what a test creates is gone when it ends. The tests must hold whichever order they run in,
/// so each records what it saw in a static and checks the records of the tests before it.
/// </summary>
public sealed class SharedHostFixtureTests : ApiTestBase<SharedHostFixtureTests.SharedHostFixture>
{
    private const string Org = "tdd";
    private const string App = "contributer-restriction";

    public interface IResettableService
    {
        string Greet(string name);
    }

    public sealed class SharedHostFixture : AppApiFixture
    {
        protected override string Org => SharedHostFixtureTests.Org;
        protected override string App => SharedHostFixtureTests.App;

        public Mock<IResettableService> ResettableService { get; private set; } = null!;

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            ResettableService = RegisterMock<IResettableService>(services);
        }
    }

    private static readonly ConcurrentBag<IServiceProvider> _hostsSeen = [];
    private static readonly ConcurrentBag<Guid> _testIdsSeen = [];
    private static readonly ConcurrentBag<InstanceIdentifier> _instancesCreatedByEarlierTests = [];

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
            $"/{Org}/{App}/api/v1/applicationmetadata",
            TestContext.Current.CancellationToken
        );
        var metadata = await VerifyStatusAndDeserialize<ApplicationMetadata>(response, HttpStatusCode.OK);
        metadata.Id.Should().Be($"{Org}/{App}");

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
    public async Task Instance_from_a_code_template_is_served_by_the_app_and_deleted_after_the_test()
    {
        await AssertInstancesOfEarlierTestsAreGone();

        var template = new InstanceTemplate(Org, App, TestAuthentication.DefaultUserPartyId)
            .InTask("Task_1")
            .WithFormData("default", new Skjema { Melding = new Dummy { Name = "Kari Nordmann" } });

        Instance instance = await CreateInstance(template);
        var identifier = new InstanceIdentifier(instance);
        _instancesCreatedByEarlierTests.Add(identifier);

        instance.InstanceOwner.PartyId.Should().Be(TestAuthentication.DefaultUserPartyId.ToString());
        instance.Process.CurrentTask.ElementId.Should().Be("Task_1");
        instance.Data.Should().ContainSingle(d => d.DataType == "default");

        using var client = await GetUserClient(partyId: TestAuthentication.DefaultUserPartyId);
        using var response = await client.GetAsync(
            $"/{Org}/{App}/instances/{instance.Id}",
            TestContext.Current.CancellationToken
        );
        var served = await VerifyStatusAndDeserialize<InstanceResponse>(response, HttpStatusCode.OK);
        served.Id.Should().Be(instance.Id);
        served.Data.Should().ContainSingle(d => d.DataType == "default");

        // What EndTestAsync does, run here so this test can see the result
        await Fixture.DeleteCreatedInstances();
        await AssertInstanceIsGone(identifier);
    }

    [Fact]
    public async Task Instance_from_a_loaded_template_equals_one_from_a_code_template()
    {
        await AssertInstancesOfEarlierTestsAreGone();

        // The prepared instance: owner 500600 in Task_1 with one "default" element, a Skjema for Per Olsen
        InstanceTemplate loaded = InstanceTemplate.Load(
            Org,
            App,
            500600,
            Guid.Parse("00000000-dead-0000-babe-000000000999")
        );
        InstanceTemplate built = new InstanceTemplate(Org, App, 500600)
        {
            InstanceOwnerOrganisationNumber = "897069631",
        }
            .InTask("Task_1")
            .WithFormData(
                "default",
                new Skjema
                {
                    Melding = new Dummy
                    {
                        Name = "Per Olsen",
                        Random = "afdetsd",
                        Tags = "ddd",
                    },
                }
            );

        Instance fromLoaded = await CreateInstance(loaded);
        Instance fromBuilt = await CreateInstance(built);
        _instancesCreatedByEarlierTests.Add(new InstanceIdentifier(fromLoaded));
        _instancesCreatedByEarlierTests.Add(new InstanceIdentifier(fromBuilt));

        fromLoaded.Id.Should().NotBe(fromBuilt.Id);
        foreach (Instance instance in new[] { fromLoaded, fromBuilt })
        {
            instance.InstanceOwner.PartyId.Should().Be("500600");
            instance.InstanceOwner.OrganisationNumber.Should().Be("897069631");
            instance.Process.CurrentTask.ElementId.Should().Be("Task_1");
            instance.Process.CurrentTask.AltinnTaskType.Should().Be("data");
            instance.Data.Should().ContainSingle(d => d.DataType == "default" && d.ContentType == "application/xml");
        }

        var skjemaFromLoaded = await ReadFormData(fromLoaded);
        var skjemaFromBuilt = await ReadFormData(fromBuilt);
        skjemaFromLoaded.Melding.Should().BeEquivalentTo(skjemaFromBuilt.Melding);
        skjemaFromLoaded.Melding!.Name.Should().Be("Per Olsen");
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
            $"/{Org}/{App}/api/v1/applicationmetadata",
            TestContext.Current.CancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<Skjema> ReadFormData(Instance instance)
    {
        await using var scope = Services.CreateAsyncScope();
        var dataClient = scope.ServiceProvider.GetRequiredService<IDataClient>();
        DataElement element = instance.Data.Single(d => d.DataType == "default");
        object formData = await dataClient.GetFormData(
            instance,
            element,
            authenticationMethod: null,
            TestContext.Current.CancellationToken
        );
        return formData.Should().BeOfType<Skjema>().Subject;
    }

    private async Task AssertInstancesOfEarlierTestsAreGone()
    {
        foreach (InstanceIdentifier instance in _instancesCreatedByEarlierTests)
        {
            await AssertInstanceIsGone(instance);
        }
    }

    /// <summary>The mock throws for a missing file where the real client gets a 404; both mean gone.</summary>
    private async Task AssertInstanceIsGone(InstanceIdentifier instance)
    {
        await using var scope = Services.CreateAsyncScope();
        var instanceClient = scope.ServiceProvider.GetRequiredService<IInstanceClient>();
        Func<Task> read = () =>
            instanceClient.GetInstance(
                App,
                Org,
                instance.InstanceOwnerPartyId,
                instance.InstanceGuid,
                authenticationMethod: null,
                TestContext.Current.CancellationToken
            );
        await read.Should().ThrowAsync<Exception>().Where(e => IsNotFound(e));
    }

    private static bool IsNotFound(Exception e) =>
        e is FileNotFoundException
        || e is Altinn.App.Core.Helpers.PlatformHttpException { Response.StatusCode: HttpStatusCode.NotFound };

    private sealed class OwnHostMarker;
}
