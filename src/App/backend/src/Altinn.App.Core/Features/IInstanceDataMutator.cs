using Altinn.App.Core.Features.FileAnalysis;
using Altinn.App.Core.Features.Validation;
using Altinn.App.Core.Models;
using Altinn.App.Core.Models.Validation;
using Altinn.Platform.Storage.Interface.Models;
using KeyValueEntry = Altinn.Platform.Storage.Interface.Models.KeyValueEntry;

namespace Altinn.App.Core.Features;

/// <summary>
/// Extension of the IInstanceDataAccessor that allows for adding, updating and removing data elements,
/// and also indicate that it is OK to mutate the models
/// </summary>
/// <remarks>
/// Changes to <see cref="IInstanceDataAccessor.Instance"/> are saved too: its <c>DataValues</c> and
/// <c>PresentationTexts</c>, and the <c>Tags</c>, <c>Metadata</c>, <c>UserDefinedMetadata</c>, <c>Refs</c> and
/// <c>Filename</c> of its data elements and of the elements this mutator adds. The save throws for a change to
/// anything else on the instance, and for a key that <c>dataFields</c> or <c>presentationFields</c> in
/// applicationmetadata.json derives.
/// </remarks>
public interface IInstanceDataMutator : IInstanceDataAccessor
{
    /// <summary>
    /// Add a new data element with app logic to the instance of this accessor
    /// </summary>
    /// <remarks>
    /// Serialization of data is done immediately, so the data object should be in a valid state.
    /// </remarks>
    /// <throws>Throws an InvalidOperationException if the dataType is not found in applicationmetadata</throws>
    FormDataChange AddFormDataElement(string dataTypeId, object model);

    /// <summary>
    /// Add a new data element with app logic to the instance of this accessor
    /// </summary>
    /// <remarks>
    /// Serialization of data is done immediately, so the data object should be in a valid state.
    /// </remarks>
    /// <throws>Throws an InvalidOperationException if the dataType is not found in applicationmetadata</throws>
    FormDataChange AddFormDataElement(DataType dataType, object model) => AddFormDataElement(dataType.Id, model);

    /// <summary>
    /// Add a new data element without app logic to the instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Saving to storage is not done until the instance is saved, so mutations to data might or might not be sent to storage.
    /// </para>
    /// <para>
    /// <c>generatedFromTask</c> marks the element for automatic deletion when the process (re-)enters that task.
    /// Use it for regenerable artifacts whose lifetime should follow the task, and leave it unset to manage the
    /// element's lifecycle explicitly/manually.
    /// </para>
    /// </remarks>
    BinaryDataChange AddBinaryDataElement(
        string dataTypeId,
        string contentType,
        string? filename,
        ReadOnlyMemory<byte> bytes,
        string? generatedFromTask = null,
        List<KeyValueEntry>? metadata = null
    );

    /// <summary>
    /// Add a new data element without app logic to the instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Saving to storage is not done until the instance is saved, so mutations to data might or might not be sent to storage.
    /// </para>
    /// <para>
    /// <c>generatedFromTask</c> marks the element for automatic deletion when the process (re-)enters that task.
    /// Use it for regenerable artifacts whose lifetime should follow the task, and leave it unset to manage the
    /// element's lifecycle explicitly/manually.
    /// </para>
    /// </remarks>
    BinaryDataChange AddBinaryDataElement(
        DataType dataType,
        string contentType,
        string? filename,
        ReadOnlyMemory<byte> bytes,
        string? generatedFromTask = null,
        List<KeyValueEntry>? metadata = null
    ) => AddBinaryDataElement(dataType.Id, contentType, filename, bytes, generatedFromTask, metadata);

    /// <summary>
    /// Replace the binary content of an existing binary data element.
    /// </summary>
    /// <remarks>
    /// Saving to storage is not done until the instance is saved, so mutations to data might or might not be sent to storage.
    /// </remarks>
    BinaryDataChange UpdateBinaryDataElement(
        DataElementIdentifier dataElementIdentifier,
        string contentType,
        ReadOnlyMemory<byte> bytes
    );

    /// <summary>
    /// Remove a data element from the instance.
    ///
    /// Actual removal from storage is not done until the instance is saved.
    /// </summary>
    void RemoveDataElement(DataElementIdentifier dataElementIdentifier);

    /// <summary>
    /// Copies <paramref name="model"/> into the form data of an existing data element.
    /// </summary>
    /// <remarks>
    /// The copy is taken at the call and holds only the fields of the data model.
    /// Make later changes to the returned object.
    /// </remarks>
    /// <returns>The element's form data, the object that <see cref="IInstanceDataAccessor.GetFormData"/> returns</returns>
    Task<T> ReplaceFormData<T>(DataElementIdentifier dataElementIdentifier, T model)
        where T : class;

    /// <summary>
    /// Stop processing the request with current changes and return a BadRequest with the following issues
    /// This is intended to be an alternative to a <see cref="IFileAnalyzer"/> <see cref="IFileValidator"/>
    /// pair to ensure that new binary data is not saved if the data is not valid.
    /// </summary>
    /// <param name="validationIssues"></param>
    void AbandonAllChanges(IEnumerable<ValidationIssue> validationIssues);
}
