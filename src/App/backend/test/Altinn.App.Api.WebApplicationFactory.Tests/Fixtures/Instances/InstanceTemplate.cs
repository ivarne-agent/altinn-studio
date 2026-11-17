using Altinn.Platform.Storage.Interface.Models;

namespace Altinn.App.Api.Tests.Fixtures.Instances;

/// <summary>
/// What a test wants an instance to look like before it starts: owner, process state and data elements. The
/// platform assigns every id when <see cref="AppTestFixture.CreateInstance"/> creates it, so one template can be
/// created any number of times.
/// </summary>
public sealed record InstanceTemplate(string Org, string App, int InstanceOwnerPartyId)
{
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
    public InstanceTemplate WithFormData(string dataType, object model, Action<DataElement>? configure = null) =>
        this with
        {
            DataElements = [.. DataElements, InstanceTemplateDataElement.FromModel(dataType, model, configure)],
        };

    /// <summary>A data element with <paramref name="content"/> stored as it is.</summary>
    public InstanceTemplate WithData(
        string dataType,
        string contentType,
        byte[] content,
        string? filename = null,
        Action<DataElement>? configure = null
    ) =>
        this with
        {
            DataElements =
            [
                .. DataElements,
                InstanceTemplateDataElement.FromContent(dataType, contentType, content, filename, configure),
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
}

/// <summary>
/// A data element of an <see cref="InstanceTemplate"/>: either a model the app serializes, or content stored as it is.
/// <see cref="Configure"/> runs on the stored element, for tags, locks and other metadata.
/// </summary>
public sealed record InstanceTemplateDataElement
{
    public required string DataType { get; init; }

    /// <summary>Serialized by the app's own serialization for the data type when set.</summary>
    public object? Model { get; init; }

    public string? ContentType { get; init; }

    public byte[]? Content { get; init; }

    public string? Filename { get; init; }

    public Action<DataElement>? Configure { get; init; }

    public static InstanceTemplateDataElement FromModel(
        string dataType,
        object model,
        Action<DataElement>? configure = null
    ) =>
        new()
        {
            DataType = dataType,
            Model = model,
            Configure = configure,
        };

    public static InstanceTemplateDataElement FromContent(
        string dataType,
        string contentType,
        byte[] content,
        string? filename = null,
        Action<DataElement>? configure = null
    ) =>
        new()
        {
            DataType = dataType,
            ContentType = contentType,
            Content = content,
            Filename = filename,
            Configure = configure,
        };
}
