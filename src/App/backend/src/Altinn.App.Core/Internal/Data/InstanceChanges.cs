using KeyValueEntry = Altinn.Platform.Storage.Interface.Models.KeyValueEntry;

namespace Altinn.App.Core.Internal.Data;

/// <summary>
/// The data values and presentation texts the app changed on the instance. A null value removes the key.
/// </summary>
internal sealed record InstanceChanges(
    IReadOnlyDictionary<string, string?> DataValues,
    IReadOnlyDictionary<string, string?> PresentationTexts
)
{
    public static readonly InstanceChanges None = new(
        new Dictionary<string, string?>(),
        new Dictionary<string, string?>()
    );

    public bool HasChanges => DataValues.Count > 0 || PresentationTexts.Count > 0;
}

/// <summary>
/// The metadata the app changed on a data element. A null property is left unchanged by the save.
/// </summary>
internal sealed record DataElementMetadataChange(
    List<string>? Tags,
    List<KeyValueEntry>? Metadata,
    List<KeyValueEntry>? UserDefinedMetadata,
    List<Guid>? Refs,
    string? Filename
)
{
    public static readonly DataElementMetadataChange None = new(null, null, null, null, null);
}
