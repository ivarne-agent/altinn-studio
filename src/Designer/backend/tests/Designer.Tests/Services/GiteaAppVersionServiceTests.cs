#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Altinn.Studio.Designer.Clients.Interfaces;
using Altinn.Studio.Designer.Models;
using Altinn.Studio.Designer.Services.Implementation;
using Moq;
using Xunit;

namespace Designer.Tests.Services;

public class GiteaAppVersionServiceTests
{
    private const string Org = "ttd";
    private const string App = "app";
    private const string Reference = "abcd1234";

    private readonly Mock<IGiteaClient> _giteaClient = new();

    [Theory]
    [InlineData("Altinn.App.Api", "8.12.15", "8.12.15")]
    [InlineData("Altinn.App.Api", "9.0.0-preview.9", "9.0.0-preview.9")]
    [InlineData("Altinn.App.Api", "[9.1.0]", "9.1.0")]
    [InlineData("Altinn.App.Api.Experimental", "8.0.0-rc1", "8.0.0-rc1")]
    public async Task GetAppLibVersionAsync_PackageReference_ReturnsVersion(
        string packageName,
        string version,
        string expected
    )
    {
        SetupCsproj(
            $"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <PackageReference Include="Some.Other.Package" Version="1.0.0" />
                <PackageReference Include="{packageName}" Version="{version}" />
              </ItemGroup>
            </Project>
            """
        );

        AppLibVersionLookup result = await CreateService().GetAppLibVersionAsync(Org, App, Reference);

        Assert.True(result.IsFound);
        Assert.Equal(AppLibVersionLookupStatus.Found, result.Status);
        Assert.Equal(expected, result.Version.ToNormalizedString());
        _giteaClient.Verify(
            c => c.GetFileAsync(Org, App, "App/App.csproj", Reference, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task GetAppLibVersionAsync_NoAppLibPackageReference_ReturnsPackageReferenceNotFound()
    {
        SetupCsproj(
            """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <ProjectReference Include="../../app-lib-dotnet/src/Altinn.App.Api/Altinn.App.Api.csproj" />
              </ItemGroup>
            </Project>
            """
        );

        AppLibVersionLookup result = await CreateService().GetAppLibVersionAsync(Org, App, Reference);

        AssertFailed(result, AppLibVersionLookupStatus.PackageReferenceNotFound);
    }

    [Fact]
    public async Task GetAppLibVersionAsync_UnparsableVersion_ReturnsPackageReferenceNotFound()
    {
        SetupCsproj(
            """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <PackageReference Include="Altinn.App.Api" Version="$(AppLibVersion)" />
              </ItemGroup>
            </Project>
            """
        );

        AppLibVersionLookup result = await CreateService().GetAppLibVersionAsync(Org, App, Reference);

        AssertFailed(result, AppLibVersionLookupStatus.PackageReferenceNotFound);
    }

    [Fact]
    public async Task GetAppLibVersionAsync_MissingFile_ReturnsCsprojNotFound()
    {
        _giteaClient
            .Setup(c => c.GetFileAsync(Org, App, "App/App.csproj", Reference, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileSystemObject?)null!);

        AppLibVersionLookup result = await CreateService().GetAppLibVersionAsync(Org, App, Reference);

        AssertFailed(result, AppLibVersionLookupStatus.CsprojNotFound);
    }

    [Fact]
    public async Task GetAppLibVersionAsync_FetchThrows_ReturnsCsprojFetchFailed()
    {
        var exception = new IOException("Could not read file.");
        _giteaClient
            .Setup(c => c.GetFileAsync(Org, App, "App/App.csproj", Reference, It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

        AppLibVersionLookup result = await CreateService().GetAppLibVersionAsync(Org, App, Reference);

        AssertFailed(result, AppLibVersionLookupStatus.CsprojFetchFailed);
        Assert.Same(exception, result.Exception);
    }

    [Fact]
    public async Task GetAppLibVersionAsync_Cancelled_Throws()
    {
        _giteaClient
            .Setup(c => c.GetFileAsync(Org, App, "App/App.csproj", Reference, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreateService().GetAppLibVersionAsync(Org, App, Reference)
        );
    }

    [Fact]
    public async Task GetAppLibVersionAsync_InvalidBase64_ReturnsCsprojDecodeFailed()
    {
        _giteaClient
            .Setup(c => c.GetFileAsync(Org, App, "App/App.csproj", Reference, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemObject { Content = "not base64!" });

        AppLibVersionLookup result = await CreateService().GetAppLibVersionAsync(Org, App, Reference);

        AssertFailed(result, AppLibVersionLookupStatus.CsprojDecodeFailed);
        Assert.IsType<FormatException>(result.Exception);
    }

    [Fact]
    public async Task GetAppLibVersionAsync_InvalidXml_ReturnsCsprojParseFailed()
    {
        SetupCsproj("<Project><ItemGroup>");

        AppLibVersionLookup result = await CreateService().GetAppLibVersionAsync(Org, App, Reference);

        AssertFailed(result, AppLibVersionLookupStatus.CsprojParseFailed);
        Assert.NotNull(result.Exception);
    }

    private GiteaAppVersionService CreateService() => new(_giteaClient.Object);

    private void SetupCsproj(string content)
    {
        _giteaClient
            .Setup(c => c.GetFileAsync(Org, App, "App/App.csproj", Reference, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemObject { Content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) });
    }

    private static void AssertFailed(AppLibVersionLookup result, AppLibVersionLookupStatus expectedStatus)
    {
        Assert.False(result.IsFound);
        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.Version);
    }
}
