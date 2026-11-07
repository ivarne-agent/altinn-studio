using Altinn.App.Analyzers.Metadata;
using Altinn.App.Analyzers.Tests.Fixtures;
using Microsoft.CodeAnalysis;

namespace Altinn.App.Analyzers.Tests.Metadata;

public class TaskIdsShapeUtilsTests
{
    private const string Path = "/repo/App/config/applicationmetadata.json";

    private static List<Diagnostic> Collect(string json)
    {
        var diagnostics = new List<Diagnostic>();
        TaskIdsShapeUtils.CollectDiagnostics(
            new InMemoryAdditionalText(Path, json),
            CancellationToken.None,
            diagnostics
        );
        return diagnostics;
    }

    private static string Metadata(params string[] dataTypes) =>
        $$"""
            {
              "id": "ttd/app",
              "dataTypes": [
                {{string.Join(",\n    ", dataTypes)}}
              ]
            }
            """;

    private static string Quoted(string json, Diagnostic diagnostic) =>
        json.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);

    [Fact]
    public void A_Number_Entry_Is_An_Error_On_The_Entry()
    {
        var json = Metadata("""{ "id": "model", "taskIds": ["Task_1", 2] }""");

        var diagnostic = Assert.Single(Collect(json));

        Assert.Equal(Diagnostics.Metadata.TaskIdsNotStrings.Id, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(
            "Data type 'model' has 2 in its taskIds, which is a number, not a string. The app reads taskIds as a list "
                + "of strings, so it does not start with this file. Write each task id as a string, such as "
                + "[\"Task_1\"], or leave taskIds out to bind the data type to no task.",
            diagnostic.GetMessage()
        );
        Assert.Equal(Path, diagnostic.Location.GetLineSpan().Path);
        Assert.Equal("2", Quoted(json, diagnostic));
    }

    [Theory]
    [InlineData("""{ "id": "model", "taskIds": [true] }""", "true", "a boolean, not a string")]
    [InlineData(
        """{ "id": "model", "taskIds": [{ "id": "Task_1" }] }""",
        "{ \"id\": \"Task_1\" }",
        "an object, not a string"
    )]
    [InlineData(
        """{ "id": "model", "taskIds": [["Task_1"]] }""",
        "[\"Task_1\"]",
        "a list inside the list, not a string"
    )]
    [InlineData("""{ "id": "model", "taskIds": "Task_1" }""", "\"Task_1\"", "a string, not a list")]
    [InlineData("""{ "id": "model", "taskIds": 1 }""", "1", "a number, not a string")]
    [InlineData(
        """{ "id": "model", "taskIds": { "Task_1": true } }""",
        "{ \"Task_1\": true }",
        "an object, not a string"
    )]
    [InlineData("""{ "ID": "model", "TASKIDS": [1] }""", "1", "a number, not a string")]
    public void Every_Value_The_Runtime_Cannot_Read_Is_An_Error_On_That_Value(
        string dataType,
        string expectedSpan,
        string expectedDescription
    )
    {
        var json = Metadata(dataType);

        var diagnostic = Assert.Single(Collect(json));

        Assert.Equal(Diagnostics.Metadata.TaskIdsNotStrings.Id, diagnostic.Id);
        Assert.Equal(expectedSpan, Quoted(json, diagnostic));
        Assert.Contains($"has {expectedSpan} in its taskIds, which is {expectedDescription}.", diagnostic.GetMessage());
    }

    [Fact]
    public void Every_Unreadable_Entry_Is_Reported_In_File_Order()
    {
        var json = Metadata(
            """{ "id": "a", "taskIds": [1, "Task_1", null, false] }""",
            """{ "id": "b", "taskIds": ["Task_2"] }""",
            """{ "id": "c", "taskIds": 3 }"""
        );

        Assert.Equal(
            [("1", "a"), ("false", "a"), ("3", "c")],
            Collect(json).Select(d => (Quoted(json, d), d.GetMessage().Substring(11, 1)))
        );
    }

    [Fact]
    public void A_Long_Value_Is_Quoted_Cut_Short()
    {
        var longName = new string('x', 60);
        var json = Metadata($$"""{ "id": "model", "taskIds": [{ "name": "{{longName}}" }] }""");

        var diagnostic = Assert.Single(Collect(json));

        Assert.Contains($"has {{ \"name\": \"{new string('x', 29)}… in its taskIds", diagnostic.GetMessage());
        Assert.Equal($$"""{ "name": "{{longName}}" }""", Quoted(json, diagnostic));
    }

    [Theory]
    [InlineData("""{ "id": "model", "taskIds": ["Task_1", "Task_2"] }""")]
    [InlineData("""{ "id": "model", "taskIds": [] }""")]
    [InlineData("""{ "id": "model", "taskIds": null }""")]
    [InlineData("""{ "id": "model" }""")]
    // A null entry is read by the runtime; ALTINNAPP1017 reports that it names no task.
    [InlineData("""{ "id": "model", "taskIds": [null] }""")]
    // A trailing comma is accepted by the runtime too.
    [InlineData("""{ "id": "model", "taskIds": ["Task_1",] }""")]
    [InlineData("""{ "id": "model", "taskId": "Task_1" }""")]
    public void A_Task_Ids_The_Runtime_Reads_Is_Valid(string dataType)
    {
        Assert.Empty(Collect(Metadata(dataType)));
    }

    [Theory]
    [InlineData("{ \"dataTypes\": [ { \"id\": \"model\", \"taskIds\": [1] ")]
    [InlineData("[]")]
    [InlineData("{ }")]
    public void Unreadable_Applicationmetadata_Is_Ignored(string json)
    {
        Assert.Empty(Collect(json));
    }
}
