using System.Net;
using System.Text;
using System.Text.Json;
using Altinn.App.Api.Models;
using Altinn.App.Api.Tests.Fixtures;
using Altinn.App.Api.Tests.Fixtures.AppFiles;
using Altinn.App.Api.Tests.Models;
using Altinn.App.Core.Internal.Process;
using Altinn.App.Core.Models;
using Altinn.Platform.Storage.Interface.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Altinn.App.Api.Tests.SystemTests;

/// <summary>
/// The system at work on an app built in code: one data type and a two-task process, instances from templates,
/// requests through the app, and app files changed per test without a new host.
/// </summary>
public sealed class CodeBuiltAppTests : ApiTestBase<CodeBuiltAppTests.AppFixture>
{
    public sealed class AppFixture : AppTestFixture
    {
        protected override AppFilesBuilder ConfigureApp() =>
            AppFilesBuilder
                .Minimal<Skjema>("ttd", "code-built-app")
                .WithProcess(BpmnBuilder.LinearProcess(("Task_1", "data"), ("Task_2", "confirmation")));
    }

    private const int Owner = TestAuthentication.DefaultUserPartyId;

    public CodeBuiltAppTests(AppFixture fixture)
        : base(fixture) { }

    [Fact]
    public async Task Instance_from_a_template_is_served_and_patched_through_the_app()
    {
        Instance instance = await CreateInstance(
            InstanceOf(Owner)
                .InTask("Task_1")
                .WithFormData("default", new Skjema { Melding = new Dummy { Name = "Ola" } })
        );
        DataElement dataElement = instance.Data.Should().ContainSingle(d => d.DataType == "default").Subject;

        using HttpClient client = await GetUserClient(partyId: Owner);
        using HttpResponseMessage getResponse = await client.GetAsync(
            $"/{Org}/{App}/instances/{instance.Id}",
            TestContext.Current.CancellationToken
        );
        var served = await VerifyStatusAndDeserialize<InstanceResponse>(getResponse, HttpStatusCode.OK);
        served.Data.Should().ContainSingle(d => d.Id == dataElement.Id);
        served.Process.CurrentTask.ElementId.Should().Be("Task_1");

        Fixture.Storage.RequestsResponses.Clear();
        using var patch = new StringContent(
            $$"""
            {"patches": [{"dataElementId": "{{dataElement.Id}}", "patch": [{"op": "replace", "path": "/melding/name", "value": "Kari"}]}]}
            """,
            Encoding.UTF8,
            "application/json"
        );
        using HttpResponseMessage patchResponse = await client.PatchAsync(
            $"/{Org}/{App}/instances/{instance.Id}/data",
            patch,
            TestContext.Current.CancellationToken
        );
        var patched = await VerifyStatusAndDeserialize<DataPatchResponseMultiple>(patchResponse, HttpStatusCode.OK);
        patched.NewDataModels.Should().ContainSingle();
        ((JsonElement)patched.NewDataModels[0].Data)
            .GetProperty("melding")
            .GetProperty("name")
            .GetString()
            .Should()
            .Be("Kari");

        // The app wrote the change to storage through its own client, and the in-memory storage holds it
        Fixture
            .Storage.RequestsResponses.Should()
            .Contain(r =>
                (r.RequestMethod == HttpMethod.Put || r.RequestMethod == HttpMethod.Post)
                && r.RequestUrl!.AbsolutePath.StartsWith($"/storage/api/v1/instances/{instance.Id}")
                && (int)r.ResponseStatusCode < 300
            );
        (Instance stored, Dictionary<string, byte[]> storedData) = Fixture.Storage.GetInstanceAndData(
            Owner,
            Guid.Parse(instance.Id.Split('/')[1])
        );
        Encoding.UTF8.GetString(storedData[dataElement.Id]).Should().Contain("<name>Kari</name>");
        stored.Data.Should().ContainSingle();
    }

    [Fact]
    public async Task UseAppFiles_changes_the_application_metadata_for_the_rest_of_the_test()
    {
        IServiceProvider servicesBefore = Services;
        using HttpClient client = await GetUserClient();

        (await GetMetadata(client)).PromptForParty.Should().BeNull("every test starts from the class's app files");

        UseAppFiles(app => app.WithApplicationMetadata(metadata => metadata.PromptForParty = "always"));

        (await GetMetadata(client)).PromptForParty.Should().Be("always");
        Services.Should().BeSameAs(servicesBefore, "a change of app files does not build a host");
    }

    [Fact]
    public async Task UseAppFiles_changes_the_process_and_the_process_reader_sees_it()
    {
        var processReader = Services.GetRequiredService<IProcessReader>();
        processReader.GetProcessTaskIds().Should().Equal("Task_1", "Task_2");

        UseAppFiles(app =>
            app.WithProcess(
                BpmnBuilder.LinearProcess(("Task_1", "data"), ("Task_2", "confirmation"), ("Task_3", "signing"))
            )
        );

        Services.GetRequiredService<IProcessReader>().Should().BeSameAs(processReader, "the reader is a singleton");
        processReader.GetProcessTaskIds().Should().Equal("Task_1", "Task_2", "Task_3");

        using HttpClient client = await GetUserClient();
        using HttpResponseMessage response = await client.GetAsync(
            $"/{Org}/{App}/api/v1/meta/process",
            TestContext.Current.CancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("Task_3");
    }

    [Fact]
    public async Task Policy_from_the_app_files_denies_a_user()
    {
        Instance instance = await CreateInstance(InstanceOf(Owner).InTask("Task_1"));
        using HttpClient client = await GetUserClient(partyId: Owner);
        string url = $"/{Org}/{App}/instances/{instance.Id}";

        using HttpResponseMessage allowed = await client.GetAsync(url, TestContext.Current.CancellationToken);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);

        // User 1337 holds REGNA and PRIV for its own party, never DAGL
        UseAppFiles(app =>
            app.WithPolicy(
                new XacmlPolicyBuilder(Org, App).Permit(new XacmlRule { RoleCodes = ["DAGL"], Actions = ["read"] })
            )
        );

        using HttpResponseMessage denied = await client.GetAsync(url, TestContext.Current.CancellationToken);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<ApplicationMetadata> GetMetadata(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync(
            $"/{Org}/{App}/api/v1/applicationmetadata",
            TestContext.Current.CancellationToken
        );
        return await VerifyStatusAndDeserialize<ApplicationMetadata>(response, HttpStatusCode.OK);
    }
}
