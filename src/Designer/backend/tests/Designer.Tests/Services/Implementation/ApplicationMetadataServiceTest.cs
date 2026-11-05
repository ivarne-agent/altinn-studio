#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Altinn.Studio.Designer.Clients.Interfaces;
using Altinn.Studio.Designer.Models;
using Altinn.Studio.Designer.Models.App;
using Altinn.Studio.Designer.Services.Implementation;
using Altinn.Studio.Designer.Services.Interfaces;
using Altinn.Studio.Designer.TypedHttpClients.AltinnStorage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NuGet.Versioning;
using Xunit;

namespace Designer.Tests.Services.Implementation;

public class ApplicationMetadataServiceTest
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateApplicationMetadataInStorageAsync_UpdatesJsonData_PassesPayloadToStorageClient(
        bool usePascalCasePropertyNames
    )
    {
        // Arrange
        var fixture = Fixture.Create();
        var appMetadata = new ApplicationMetadata("old-org/old-app");
        string? capturedAppMetadataJson = null;
        var jsonSerializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = usePascalCasePropertyNames ? JsonNamingPolicy.CamelCase : null,
        };

        fixture
            .MockGiteaClient.Setup(x =>
                x.GetFileAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(Base64FileSystemObjectFactory(appMetadata, jsonSerializerOptions));

        fixture
            .MockStorageAppMetadataClient.Setup(x =>
                x.UpsertApplicationMetadata(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>()
                )
            )
            .Callback(
                (string org, string app, string applicationMetadataJson, string envName) =>
                {
                    capturedAppMetadataJson = applicationMetadataJson;
                }
            );

        // Act
        string appMetadataJson = await fixture.Service.GetApplicationMetadataJsonFromSpecificReference(
            "new-org",
            "new-app",
            "abcd1234"
        );
        await fixture.Service.UpdateApplicationMetadataInStorageAsync(
            "new-org",
            "new-app",
            appMetadataJson,
            "abcd1234",
            "tt02",
            CancellationToken.None
        );

        // Assert
        var capturedAppMetadata = JsonSerializer.Deserialize<ApplicationMetadata>(
            capturedAppMetadataJson!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true } // Simulate usage in AltinnStorageAppMetadataClient.GetApplicationMetadataAsync
        );
        Assert.NotNull(capturedAppMetadata);
        Assert.Equal("new-org/new-app", capturedAppMetadata.Id);
        Assert.Equal("abcd1234", capturedAppMetadata.VersionId);
    }

    [Theory]
    [InlineData("""{"id":"ttd/app","altinnNugetVersion":"1.0.0"}""", "altinnNugetVersion")]
    [InlineData("""{"Id":"ttd/app","AltinnNugetVersion":"1.0.0"}""", "AltinnNugetVersion")]
    public async Task UpdateApplicationMetadataInStorageAsync_AppLibVersionFound_SetsAltinnNugetVersion(
        string appMetadataJson,
        string expectedKey
    )
    {
        // Arrange
        var fixture = Fixture.Create();
        fixture
            .MockGiteaAppVersionService.Setup(x =>
                x.GetAppLibVersionAsync("new-org", "new-app", "abcd1234", It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(AppLibVersionLookup.Found(SemanticVersion.Parse("9.0.0-preview.9")));

        // Act
        await fixture.Service.UpdateApplicationMetadataInStorageAsync(
            "new-org",
            "new-app",
            appMetadataJson,
            "abcd1234",
            "tt02",
            CancellationToken.None
        );

        // Assert
        JsonObject captured = fixture.GetUpsertedJson();
        Assert.Equal("9.0.0-preview.9", captured[expectedKey]!.GetValue<string>());
        Assert.Equal(3, captured.Count);
    }

    [Theory]
    [InlineData(AppLibVersionLookupStatus.CsprojNotFound)]
    [InlineData(AppLibVersionLookupStatus.CsprojFetchFailed)]
    [InlineData(AppLibVersionLookupStatus.CsprojParseFailed)]
    [InlineData(AppLibVersionLookupStatus.PackageReferenceNotFound)]
    public async Task UpdateApplicationMetadataInStorageAsync_AppLibVersionUnknown_RemovesAltinnNugetVersion(
        AppLibVersionLookupStatus status
    )
    {
        // Arrange
        var fixture = Fixture.Create();
        fixture
            .MockGiteaAppVersionService.Setup(x =>
                x.GetAppLibVersionAsync("new-org", "new-app", "abcd1234", It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(AppLibVersionLookup.Failed(status));

        // Act
        await fixture.Service.UpdateApplicationMetadataInStorageAsync(
            "new-org",
            "new-app",
            """{"id":"ttd/app","altinnNugetVersion":"9.9.9"}""",
            "abcd1234",
            "tt02",
            CancellationToken.None
        );

        // Assert
        JsonObject captured = fixture.GetUpsertedJson();
        Assert.False(captured.ContainsKey("altinnNugetVersion"));
        Assert.Equal("new-org/new-app", captured["id"]!.GetValue<string>());
        Assert.Equal("abcd1234", captured["versionId"]!.GetValue<string>());
    }

    private sealed record Fixture(
        ApplicationMetadataService ApplicationMetadataService,
        Mock<IAltinnStorageAppMetadataClient> MockStorageAppMetadataClient,
        Mock<IAltinnGitRepositoryFactory> MockAltinnGitRepositoryFactory,
        Mock<IGiteaClient> MockGiteaClient,
        Mock<IGiteaAppVersionService> MockGiteaAppVersionService
    )
    {
        public ApplicationMetadataService Service => ApplicationMetadataService;

        public JsonObject GetUpsertedJson()
        {
            string json = Assert.Single(MockStorageAppMetadataClient.Invocations).Arguments[2] as string ?? "";
            return JsonNode.Parse(json)!.AsObject();
        }

        public static Fixture Create()
        {
            var mockStorageAppMetadataClient = new Mock<IAltinnStorageAppMetadataClient>();
            var mockAltinnGitRepositoryFactory = new Mock<IAltinnGitRepositoryFactory>();
            var mockGiteaClient = new Mock<IGiteaClient>();
            var mockGiteaAppVersionService = new Mock<IGiteaAppVersionService>();
            mockGiteaAppVersionService
                .Setup(x =>
                    x.GetAppLibVersionAsync(
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .ReturnsAsync(AppLibVersionLookup.Failed(AppLibVersionLookupStatus.CsprojNotFound));

            var service = new ApplicationMetadataService(
                NullLogger<ApplicationMetadataService>.Instance,
                mockStorageAppMetadataClient.Object,
                mockAltinnGitRepositoryFactory.Object,
                Mock.Of<IHttpContextAccessor>(),
                mockGiteaClient.Object,
                mockGiteaAppVersionService.Object
            );

            return new Fixture(
                service,
                mockStorageAppMetadataClient,
                mockAltinnGitRepositoryFactory,
                mockGiteaClient,
                mockGiteaAppVersionService
            );
        }
    }

    private FileSystemObject Base64FileSystemObjectFactory<T>(
        T content,
        JsonSerializerOptions? jsonSerializerOptions = null
    )
    {
        string serializedString = JsonSerializer.Serialize(content, jsonSerializerOptions);
        byte[] plainBytes = System.Text.Encoding.UTF8.GetBytes(serializedString);
        string base64String = Convert.ToBase64String(plainBytes);

        return new FileSystemObject
        {
            Content = base64String,
            Encoding = "base64",
            Type = "file",
        };
    }
}
