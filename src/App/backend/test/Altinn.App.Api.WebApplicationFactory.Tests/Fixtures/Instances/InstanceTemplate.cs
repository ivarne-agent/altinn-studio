using System.Text.Json;
using System.Text.Json.Serialization;
using Altinn.App.Api.Tests.Data;
using Altinn.Platform.Storage.Interface.Models;

namespace Altinn.App.Api.Tests.Fixtures.Instances;

/// <summary>
/// What a test wants an instance to look like before it starts: owner, process state and data elements. The
/// platform assigns every id when <see cref="AppApiFixture.CreateInstance"/> creates it, so one template can be
/// created any number of times. Build one in code, or <see cref="Load"/> a prepared instance from the test data.
/// </summary>
public sealed record InstanceTemplate(string Org, string App, int InstanceOwnerPartyId)
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public string? InstanceOwnerPersonNumber { get; init; }

    public string? InstanceOwnerOrganisationNumber { get; init; }

    /// <summary>The process state the instance starts in; <c>null</c> for an instance whose process has not started.</summary>
    public ProcessState? Process { get; init; }

    public InstanceStatus? Status { get; init; }

    public Dictionary<string, string>? DataValues { get; init; }

    public IReadOnlyList<InstanceTemplateDataElement> DataElements { get; init; } = [];

    public string AppId => $"{Org}/{App}";

    /// <summary>The instance sits in <paramref name="taskId"/>, started a moment ago.</summary>
    public InstanceTemplate InTask(string taskId, string altinnTaskType = "data", string startEvent = "StartEvent_1")
    {
        DateTime now = DateTime.UtcNow;
        return this with
        {
            Process = new ProcessState
            {
                Started = now,
                StartEvent = startEvent,
                CurrentTask = new ProcessElementInfo
                {
                    Flow = 2,
                    Started = now,
                    ElementId = taskId,
                    AltinnTaskType = altinnTaskType,
                },
            },
        };
    }

    /// <summary>A data element with <paramref name="model"/> serialized the way the app stores that data type.</summary>
    public InstanceTemplate WithFormData(string dataType, object model) =>
        this with
        {
            DataElements = [.. DataElements, InstanceTemplateDataElement.FromModel(dataType, model)],
        };

    /// <summary>A data element with <paramref name="content"/> stored as it is.</summary>
    public InstanceTemplate WithData(string dataType, string contentType, byte[] content, string? filename = null) =>
        this with
        {
            DataElements =
            [
                .. DataElements,
                InstanceTemplateDataElement.FromContent(dataType, contentType, content, filename),
            ],
        };

    /// <summary>The instance handed to the platform's create call: everything but ids and data elements.</summary>
    public Instance ToInstance() =>
        new()
        {
            AppId = AppId,
            Org = Org,
            InstanceOwner = new InstanceOwner
            {
                PartyId = InstanceOwnerPartyId.ToString(),
                PersonNumber = InstanceOwnerPersonNumber,
                OrganisationNumber = InstanceOwnerOrganisationNumber,
            },
            Process = Process,
            Status = Status,
            DataValues = DataValues,
        };

    /// <summary>
    /// Reads a prepared instance from the test data of Altinn.App.Api.Tests
    /// (<c>Data/Instances/{org}/{app}/{partyId}/{guid}.pretest.json</c> and the data elements in <c>{guid}/</c>) as
    /// a template. Ids are dropped, so creating the template gives a new instance with the same owner, process
    /// state and data. Data elements come from the data folder only, as they did for the prepared instance.
    /// </summary>
    public static InstanceTemplate Load(string org, string app, int instanceOwnerPartyId, Guid instanceGuid)
    {
        string instanceDirectory = Path.Join(
            TestData.GetInstancesDirectory(),
            org,
            app,
            instanceOwnerPartyId.ToString()
        );
        string instancePath = Path.Join(instanceDirectory, $"{instanceGuid}.pretest.json");
        Instance stored = ReadJson<Instance>(instancePath);

        var dataElements = new List<InstanceTemplateDataElement>();
        string dataDirectory = Path.Join(instanceDirectory, instanceGuid.ToString());
        if (Directory.Exists(dataDirectory))
        {
            foreach (
                string elementPath in Directory
                    .EnumerateFiles(dataDirectory, "*.pretest.json", SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.Ordinal)
            )
            {
                DataElement element = ReadJson<DataElement>(elementPath);
                string blobPath = Path.Join(dataDirectory, "blob", $"{element.Id}.pretest");
                byte[] content = File.Exists(blobPath) ? File.ReadAllBytes(blobPath) : [];
                dataElements.Add(
                    InstanceTemplateDataElement.FromContent(
                        element.DataType,
                        element.ContentType ?? "application/octet-stream",
                        content,
                        element.Filename
                    )
                );
            }
        }

        return new InstanceTemplate(org, app, instanceOwnerPartyId)
        {
            InstanceOwnerPersonNumber = stored.InstanceOwner?.PersonNumber,
            InstanceOwnerOrganisationNumber = stored.InstanceOwner?.OrganisationNumber,
            Process = stored.Process,
            Status = stored.Status,
            DataValues = stored.DataValues,
            DataElements = dataElements,
        };
    }

    private static T ReadJson<T>(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No prepared instance file at {path}", path);
        }
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, _jsonOptions)
            ?? throw new InvalidDataException($"{path} did not deserialize to {typeof(T).Name}");
    }
}

/// <summary>
/// A data element of an <see cref="InstanceTemplate"/>: either a model the app serializes, or content stored as it is.
/// </summary>
public sealed record InstanceTemplateDataElement
{
    public required string DataType { get; init; }

    /// <summary>Serialized by the app's own serialization for the data type when set.</summary>
    public object? Model { get; init; }

    public string? ContentType { get; init; }

    public byte[]? Content { get; init; }

    public string? Filename { get; init; }

    public static InstanceTemplateDataElement FromModel(string dataType, object model) =>
        new() { DataType = dataType, Model = model };

    public static InstanceTemplateDataElement FromContent(
        string dataType,
        string contentType,
        byte[] content,
        string? filename = null
    ) =>
        new()
        {
            DataType = dataType,
            ContentType = contentType,
            Content = content,
            Filename = filename,
        };
}
