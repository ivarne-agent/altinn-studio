#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Altinn.Studio.Designer.Models.App;
using Xunit;

namespace Designer.Tests.Models.App;

// AltinnNugetVersion is declared on Storage's Application, again on App.Core's ApplicationMetadata (which defaults it
// to the App.Core assembly version) and hidden once more by Designer's ApplicationMetadata. System.Text.Json, which
// Designer uses for this type, must bind only the Designer property.
public class ApplicationMetadataSerializationTests
{
    private static readonly JsonSerializerOptions s_repositoryJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void SystemTextJson_RoundTripsAltinnNugetVersion()
    {
        var metadata = new ApplicationMetadata("ttd/app") { AltinnNugetVersion = "9.0.0-preview.9" };

        string json = JsonSerializer.Serialize(metadata, s_repositoryJsonOptions);
        ApplicationMetadata? result = JsonSerializer.Deserialize<ApplicationMetadata>(json, s_repositoryJsonOptions);

        Assert.Equal("9.0.0-preview.9", JsonNode.Parse(json)!["altinnNugetVersion"]!.GetValue<string>());
        Assert.Equal("9.0.0-preview.9", result!.AltinnNugetVersion);
    }

    [Fact]
    public void SystemTextJson_UnsetAltinnNugetVersion_IsOmitted()
    {
        string json = JsonSerializer.Serialize(new ApplicationMetadata("ttd/app"), s_repositoryJsonOptions);

        Assert.False(JsonNode.Parse(json)!.AsObject().ContainsKey("altinnNugetVersion"));
    }

    [Fact]
    public void SystemTextJson_ReadsPascalCaseAltinnNugetVersion()
    {
        ApplicationMetadata? result = JsonSerializer.Deserialize<ApplicationMetadata>(
            """{"Id":"ttd/app","AltinnNugetVersion":"8.12.15"}""",
            s_repositoryJsonOptions
        );

        Assert.Equal("8.12.15", result!.AltinnNugetVersion);
    }
}
