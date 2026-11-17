using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Altinn.App.Api.Models;
using Altinn.App.Api.Tests.Fixtures;
using Altinn.App.Api.Tests.Fixtures.AppFiles;
using Altinn.App.Api.Tests.Models;
using Altinn.Platform.Storage.Interface.Enums;
using Altinn.Platform.Storage.Interface.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;

namespace Altinn.App.Api.Tests.Controllers;

public sealed class DataTagsControllerTests : ApiTestBase<DataTagsControllerTests.AppFixture>
{
    public sealed class AppFixture : AppTestFixture
    {
        protected override AppFilesBuilder ConfigureApp() =>
            AppFilesBuilder
                .FromDirectory(TestApps.Directory("contributer-restriction"))
                .WithDataType<Skjema>("default");
    }

    private const int InstanceOwnerPartyId = 500600;
    private const string PreExistingTag = "Tag1";
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public DataTagsControllerTests(AppFixture fixture)
        : base(fixture) { }

    /// <summary>An instance in Task_1 whose one data element carries <see cref="PreExistingTag"/>, and a client for its owner.</summary>
    private async Task<(HttpClient Client, Instance Instance, Guid DataGuid)> Arrange()
    {
        Instance instance = await CreateInstance(
            InstanceOf(InstanceOwnerPartyId)
                .InTask("Task_1")
                .WithData(
                    "default",
                    "application/xml",
                    Encoding.UTF8.GetBytes(
                        """<?xml version="1.0" encoding="utf-8"?><Skjema xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema"><melding><name>Ola Olsen</name><toggle>true</toggle></melding></Skjema>"""
                    ),
                    configure: element => element.Tags = [PreExistingTag]
                ) with
            {
                InstanceOwnerPersonNumber = "01039012345",
            }
        );
        HttpClient client = await GetUserClient(1337, InstanceOwnerPartyId);
        return (client, instance, Guid.Parse(instance.Data.Single().Id));
    }

    private string TagsUrl(Instance instance, Guid dataGuid) =>
        $"/{Org}/{App}/instances/{instance.Id}/data/{dataGuid}/tags";

    #region GET endpoint tests

    [Fact]
    public async Task GetTags_ValidRequest_ReturnsOkWithTagsList()
    {
        // Arrange
        (HttpClient client, Instance instance, Guid dataGuid) = await Arrange();

        // Act
        var response = await client.GetAsync(TagsUrl(instance, dataGuid), TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var tagsList = JsonSerializer.Deserialize<TagsList>(responseContent, _jsonOptions);

        tagsList.Should().NotBeNull();
        tagsList.Tags.Should().NotBeNull();
        tagsList.Tags.Count.Should().Be(1);
        tagsList.Tags.Should().Contain(PreExistingTag);
    }

    [Fact]
    public async Task GetTags_DataElementNotFound_ReturnsNotFound()
    {
        // Arrange
        Guid nonExistentDataGuid = new("99999999-9999-9999-9999-999999999999");
        (HttpClient client, Instance instance, _) = await Arrange();

        // Act
        var response = await client.GetAsync(
            TagsUrl(instance, nonExistentDataGuid),
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var responseContent = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        responseContent.Should().Contain("Unable to find data element");
    }

    #endregion

    #region POST endpoint tests

    [Fact]
    public async Task AddTag_ValidTag_ReturnsCreatedWithTagsList()
    {
        // Arrange
        (HttpClient client, Instance instance, Guid dataGuid) = await Arrange();

        string newTag = "newValidTag";
        var json = JsonSerializer.Serialize(newTag);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync(
            TagsUrl(instance, dataGuid),
            content,
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var responseContent = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var tagsList = JsonSerializer.Deserialize<TagsList>(responseContent, _jsonOptions);

        tagsList.Should().NotBeNull();
        tagsList.Tags.Count.Should().Be(2);
        tagsList.Tags.Should().Contain(PreExistingTag); // pre-existing tag
        tagsList.Tags.Should().Contain(newTag); // new tag

        // Verify Location header is set
        response.Headers.Location.Should().NotBeNull();
    }

    [Theory]
    [InlineData("123")]
    [InlineData("tag@")]
    [InlineData("tag with spaces")]
    [InlineData("tag!")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AddTag_InvalidTagFormat_ReturnsBadRequest(string? invalidTag)
    {
        // Arrange
        (HttpClient client, Instance instance, Guid dataGuid) = await Arrange();

        var json = JsonSerializer.Serialize(invalidTag);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync(
            TagsUrl(instance, dataGuid),
            content,
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var responseContent = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var problemDetails = JsonSerializer.Deserialize<ProblemDetails>(responseContent, _jsonOptions);

        problemDetails.Should().NotBeNull();
        problemDetails.Status.Should().Be(400);
    }

    [Fact]
    public async Task AddTag_DuplicateTag_ReturnsCreatedWithoutDuplicate()
    {
        // Arrange
        (HttpClient client, Instance instance, Guid dataGuid) = await Arrange();

        string duplicateTag = "duplicateTag";

        // First, add the tag
        var json = JsonSerializer.Serialize(duplicateTag);
        using var content1 = new StringContent(json, Encoding.UTF8, "application/json");
        await client.PostAsync(TagsUrl(instance, dataGuid), content1, TestContext.Current.CancellationToken);

        // Now try to add the same tag again
        using var content2 = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync(
            TagsUrl(instance, dataGuid),
            content2,
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var responseContent = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var tagsList = JsonSerializer.Deserialize<TagsList>(responseContent, _jsonOptions);

        tagsList.Should().NotBeNull();
        tagsList.Tags.Count.Should().Be(2);
        tagsList.Tags.Should().Contain(PreExistingTag); // pre-existing tag
        tagsList.Tags.Count(t => t == duplicateTag).Should().Be(1, "duplicate tags should not be added");
    }

    [Fact]
    public async Task AddTag_DataElementNotFound_ReturnsNotFound()
    {
        // Arrange
        Guid nonExistentDataGuid = new("99999999-9999-9999-9999-999999999999");
        (HttpClient client, Instance instance, _) = await Arrange();

        string validTag = "validTag";
        var json = JsonSerializer.Serialize(validTag);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync(
            TagsUrl(instance, nonExistentDataGuid),
            content,
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var responseContent = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        responseContent
            .Should()
            .Contain(
                $"The instance {instance.Id} doesn't have a data element with id '99999999-9999-9999-9999-999999999999"
            );
    }

    #endregion

    #region DELETE endpoint tests

    [Fact]
    public async Task SetTags_WhenProcessing_ReturnsSharedProblemBeforeUpdate()
    {
        (HttpClient client, Instance instance, Guid dataGuid) = await Arrange();
        // The instance the fixture created is the one storage holds, so this is the stored state
        instance.Process.Status = ProcessStatus.Processing;

        using JsonContent content = JsonContent.Create(new SetTagsRequest { Tags = ["ChangedTag"] });

        using HttpResponseMessage response = await client.PutAsync(
            TagsUrl(instance, dataGuid),
            content,
            TestContext.Current.CancellationToken
        );

        await ProcessStatusProblemAssertions.AssertResponse(response, ProcessStatus.Processing);
        using HttpResponseMessage getResponse = await client.GetAsync(
            TagsUrl(instance, dataGuid),
            TestContext.Current.CancellationToken
        );
        TagsList storedTags = await VerifyStatusAndDeserialize<TagsList>(getResponse, HttpStatusCode.OK);
        storedTags.Tags.Should().Equal(PreExistingTag);
    }

    [Fact]
    public async Task DeleteTag_ExistingTag_ReturnsNoContent()
    {
        // Arrange
        (HttpClient client, Instance instance, Guid dataGuid) = await Arrange();

        // Act - Delete the tag
        var response = await client.DeleteAsync(
            $"{TagsUrl(instance, dataGuid)}/{PreExistingTag}",
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify the tag was actually removed by checking the tags list
        var getResponse = await client.GetAsync(TagsUrl(instance, dataGuid), TestContext.Current.CancellationToken);
        var getContent = await getResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var tagsList = JsonSerializer.Deserialize<TagsList>(getContent, _jsonOptions);

        tagsList.Should().NotBeNull();
        tagsList.Tags.Count.Should().Be(0);
    }

    [Fact]
    public async Task DeleteTag_NonExistentTag_ReturnsNoContent()
    {
        // Arrange
        (HttpClient client, Instance instance, Guid dataGuid) = await Arrange();

        string nonExistentTag = "nonExistentTag";

        // Act
        var response = await client.DeleteAsync(
            $"{TagsUrl(instance, dataGuid)}/{nonExistentTag}",
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteTag_DataElementNotFound_ReturnsNotFound()
    {
        // Arrange
        Guid nonExistentDataGuid = new("99999999-9999-9999-9999-999999999999");
        (HttpClient client, Instance instance, _) = await Arrange();

        string tagToDelete = "tagToDelete";

        // Act
        var response = await client.DeleteAsync(
            $"{TagsUrl(instance, nonExistentDataGuid)}/{tagToDelete}",
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var responseContent = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        responseContent
            .Should()
            .Contain(
                $"The instance {instance.Id} doesn't have a data element with id '99999999-9999-9999-9999-999999999999"
            );
    }

    #endregion

    #region PUT endpoint tests

    [Fact]
    public async Task SetTags_ValidRequest_ReturnsOkWithTags()
    {
        // Arrange
        (HttpClient client, Instance instance, Guid dataGuid) = await Arrange();

        var setTagsRequest = new SetTagsRequest { Tags = ["tagA", "tagB", "tagC"] };

        var json = JsonSerializer.Serialize(setTagsRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PutAsync(
            TagsUrl(instance, dataGuid),
            content,
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var setTagsResponse = JsonSerializer.Deserialize<SetTagsResponse>(responseContent, _jsonOptions);

        setTagsResponse.Should().NotBeNull();
        setTagsResponse.Tags.Should().BeEquivalentTo(["tagA", "tagB", "tagC"]);
        setTagsResponse.ValidationIssues.Should().BeNull();
    }

    [Fact]
    public async Task SetTags_EmptyTagsList_ClearsAllTags()
    {
        // Arrange
        (HttpClient client, Instance instance, Guid dataGuid) = await Arrange();

        var setTagsRequest = new SetTagsRequest { Tags = [] };

        var json = JsonSerializer.Serialize(setTagsRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PutAsync(
            TagsUrl(instance, dataGuid),
            content,
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var setTagsResponse = JsonSerializer.Deserialize<SetTagsResponse>(responseContent, _jsonOptions);

        setTagsResponse.Should().NotBeNull();
        setTagsResponse.Tags.Should().BeEmpty();
    }

    [Theory]
    [InlineData("123")]
    [InlineData("tag@")]
    [InlineData("tag with spaces")]
    [InlineData("tag!")]
    public async Task SetTags_InvalidTagFormat_ReturnsBadRequest(string invalidTag)
    {
        // Arrange
        (HttpClient client, Instance instance, Guid dataGuid) = await Arrange();

        var setTagsRequest = new SetTagsRequest { Tags = ["validTag", invalidTag] };

        var json = JsonSerializer.Serialize(setTagsRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PutAsync(
            TagsUrl(instance, dataGuid),
            content,
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var responseContent = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var problemDetails = JsonSerializer.Deserialize<ProblemDetails>(responseContent, _jsonOptions);

        problemDetails.Should().NotBeNull();
        problemDetails.Title.Should().Be("Invalid tag name");
        problemDetails.Detail.Should().Contain("letters");
        problemDetails.Status.Should().Be(400);
    }

    [Fact]
    public async Task SetTags_DataElementNotFound_ReturnsNotFound()
    {
        // Arrange
        Guid nonExistentDataGuid = new("99999999-9999-9999-9999-999999999999"); // Non-existent data element
        (HttpClient client, Instance instance, _) = await Arrange();

        var setTagsRequest = new SetTagsRequest { Tags = ["tagA"] };

        var json = JsonSerializer.Serialize(setTagsRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PutAsync(
            TagsUrl(instance, nonExistentDataGuid),
            content,
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var responseContent = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        responseContent
            .Should()
            .Contain(
                $"The instance {instance.Id} doesn't have a data element with id '99999999-9999-9999-9999-999999999999"
            );
    }

    [Fact]
    public async Task SetTags_WithIgnoredValidatorsQueryParameter_ReturnsOkWithValidationIssues()
    {
        // Arrange
        (HttpClient client, Instance instance, Guid dataGuid) = await Arrange();

        var setTagsRequest = new SetTagsRequest { Tags = ["tagA", "tagB"] };

        var json = JsonSerializer.Serialize(setTagsRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PutAsync(
            $"{TagsUrl(instance, dataGuid)}?ignoredValidators=Validator1,Validator2",
            content,
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var setTagsResponse = JsonSerializer.Deserialize<SetTagsResponse>(responseContent, _jsonOptions);

        setTagsResponse.Should().NotBeNull();
        setTagsResponse.Tags.Should().BeEquivalentTo(["tagA", "tagB"]);
        setTagsResponse.ValidationIssues.Should().NotBeNull();
        setTagsResponse.ValidationIssues.Should().HaveCount(2);
        setTagsResponse
            .ValidationIssues.Should()
            .Contain(validationSourcePair => validationSourcePair.Source == "Expression");
        setTagsResponse
            .ValidationIssues.Should()
            .Contain(validationSourcePair => validationSourcePair.Source == "Required");
    }

    [Fact]
    public async Task SetTags_WithoutIgnoredValidatorsQueryParameter_ReturnsOkWithoutValidationIssues()
    {
        // Arrange
        (HttpClient client, Instance instance, Guid dataGuid) = await Arrange();

        var setTagsRequest = new SetTagsRequest { Tags = ["tagA", "tagB"] };

        var json = JsonSerializer.Serialize(setTagsRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PutAsync(
            TagsUrl(instance, dataGuid),
            content,
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var setTagsResponse = JsonSerializer.Deserialize<SetTagsResponse>(responseContent, _jsonOptions);

        setTagsResponse.Should().NotBeNull();
        setTagsResponse.Tags.Should().BeEquivalentTo(["tagA", "tagB"]);
        setTagsResponse.ValidationIssues.Should().BeNull();
    }

    [Theory]
    [InlineData("valid-tag")]
    [InlineData("validTag")]
    [InlineData("valid_tag")]
    [InlineData("åæø")]
    [InlineData("TagWithÜmlaut")]
    public async Task SetTags_ValidTagFormats_ReturnsOk(string validTag)
    {
        // Arrange
        (HttpClient client, Instance instance, Guid dataGuid) = await Arrange();

        var setTagsRequest = new SetTagsRequest { Tags = [validTag] };

        var json = JsonSerializer.Serialize(setTagsRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PutAsync(
            TagsUrl(instance, dataGuid),
            content,
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var setTagsResponse = JsonSerializer.Deserialize<SetTagsResponse>(responseContent, _jsonOptions);

        setTagsResponse.Should().NotBeNull();
        setTagsResponse.Tags.Should().Contain(validTag);
    }

    #endregion
}
