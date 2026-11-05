#nullable enable
using System.Text.Json.Nodes;
using Altinn.Studio.Designer.Helpers;
using Xunit;

namespace Designer.Tests.Helpers;

public class ApplicationMetadataJsonHelperTests
{
    [Theory]
    [InlineData("""{"id":"ttd/app"}""", "altinnNugetVersion")]
    [InlineData("""{"Id":"ttd/app"}""", "AltinnNugetVersion")]
    public void SetAltinnNugetVersion_SetsVersionWithMatchingCasing(string json, string expectedKey)
    {
        string result = ApplicationMetadataJsonHelper.SetAltinnNugetVersion(json, "9.0.0-preview.9");

        JsonObject jsonObj = JsonNode.Parse(result)!.AsObject();
        Assert.Equal("9.0.0-preview.9", jsonObj[expectedKey]!.GetValue<string>());
        Assert.Equal(2, jsonObj.Count);
    }

    [Theory]
    [InlineData("""{"id":"ttd/app","altinnNugetVersion":"1.0.0"}""")]
    [InlineData("""{"id":"ttd/app","AltinnNugetVersion":"1.0.0"}""")]
    [InlineData("""{"id":"ttd/app","altinnNugetVersion":"1.0.0","AltinnNugetVersion":"2.0.0"}""")]
    public void SetAltinnNugetVersion_ReplacesExistingValueInAnyCasing(string json)
    {
        string result = ApplicationMetadataJsonHelper.SetAltinnNugetVersion(json, "8.12.15");

        Assert.Equal("""{"id":"ttd/app","altinnNugetVersion":"8.12.15"}""", result);
    }

    [Theory]
    [InlineData("""{"id":"ttd/app","altinnNugetVersion":"1.0.0"}""", """{"id":"ttd/app"}""")]
    [InlineData("""{"Id":"ttd/app","AltinnNugetVersion":"1.0.0"}""", """{"Id":"ttd/app"}""")]
    [InlineData("""{"id":"ttd/app","AltinnNugetVersion":"1.0.0"}""", """{"id":"ttd/app"}""")]
    [InlineData("""{"id":"ttd/app"}""", """{"id":"ttd/app"}""")]
    public void SetAltinnNugetVersion_NullVersion_RemovesExistingValue(string json, string expected)
    {
        string result = ApplicationMetadataJsonHelper.SetAltinnNugetVersion(json, null);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void SetAltinnNugetVersion_LeavesOtherPropertiesUntouched()
    {
        string json = """{"id":"ttd/app","title":{"nb":"Tittel"},"dataTypes":[{"id":"model"}]}""";

        string result = ApplicationMetadataJsonHelper.SetAltinnNugetVersion(json, "8.12.15");

        Assert.Equal(
            """{"id":"ttd/app","title":{"nb":"Tittel"},"dataTypes":[{"id":"model"}],"altinnNugetVersion":"8.12.15"}""",
            result
        );
    }
}
