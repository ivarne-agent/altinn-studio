using Altinn.App.Analyzers.Utils;
using NanoJsonReader;

namespace Altinn.App.Analyzers.Metadata;

/// <summary>
/// Reports a <c>taskIds</c> in <c>applicationmetadata.json</c> that the runtime cannot read. The runtime deserializes
/// <c>taskIds</c> into a <c>List&lt;string&gt;</c> with System.Text.Json, which accepts strings and nulls and throws
/// on any other entry, and on a <c>taskIds</c> that is not an array or null, so the app does not start. Null entries
/// are left to ALTINNAPP1017, which reports them as naming no task.
/// </summary>
internal static class TaskIdsShapeUtils
{
    /// <summary>
    /// Appends a diagnostic on every value in a <c>taskIds</c> that the runtime cannot read.
    /// </summary>
    internal static void CollectDiagnostics(AdditionalText text, CancellationToken token, List<Diagnostic> diagnostics)
    {
        // Unreadable files are reported by the FormDataWrapperAnalyzer (ALTINNAPP0002).
        if (MetadataFile.TryRead(text, token) is not { } metadata)
        {
            return;
        }

        foreach (var dataType in metadata.DataTypes.All)
        {
            foreach (var value in dataType.UnreadableTaskIds)
            {
                diagnostics.Add(
                    Diagnostic.Create(
                        Diagnostics.Metadata.TaskIdsNotStrings,
                        FileLocationHelper.GetLocation(text, value.Start, value.End),
                        dataType.Id,
                        value.Json,
                        Describe(value.Type)
                    )
                );
            }
        }
    }

    private static string Describe(JsonType type) =>
        type switch
        {
            JsonType.Number => "a number, not a string",
            JsonType.Boolean => "a boolean, not a string",
            JsonType.Object => "an object, not a string",
            JsonType.Array => "a list inside the list, not a string",
            JsonType.String => "a string, not a list",
            _ => "not a string",
        };
}
