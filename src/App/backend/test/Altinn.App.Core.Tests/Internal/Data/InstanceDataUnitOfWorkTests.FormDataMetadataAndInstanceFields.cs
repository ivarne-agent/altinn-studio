using System.Text.Json;
using Altinn.App.Core.Features;
using Altinn.App.Core.Internal.App;
using Altinn.App.Core.Internal.Data;
using Altinn.App.Core.Internal.Storage;
using Altinn.App.Core.Models;
using Altinn.Platform.Storage.Interface.Models;
using RequestResponse = Altinn.App.Tests.Common.Mocks.StorageClientInterceptor.RequestResponse;

namespace Altinn.App.Core.Tests.Internal.Data;

public sealed partial class InstanceDataUnitOfWorkTests
{
    [Fact]
    public async Task ReplaceFormData_SavesTheReplacement_AndGetFormDataReturnsIt()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("{}"u8.ToArray(), blobVersionId: BlobVersion(1));
        DataElement formDataElement = AddPersistedPaymentForm(
            setup,
            new PaymentForm { Status = "created", CustomerName = "Original" },
            BlobVersion(1)
        );
        var replacement = new PaymentForm { Status = "replaced", CustomerName = "Replaced" };

        await setup.DataMutator.ReplaceFormData(formDataElement, replacement);
        PaymentForm current = await setup.DataMutator.GetFormData<PaymentForm>(formDataElement);
        await setup.DataMutator.SaveChanges(setup.DataMutator.GetDataElementChanges(initializeAltinnRowId: false));

        Assert.Equal("replaced", current.Status);
        Assert.Equal("Replaced", current.CustomerName);
        Assert.Equal(SerializePaymentForm(setup, formDataElement, replacement), GetStoredData(setup, formDataElement));
    }

    [Fact]
    public async Task ReplaceFormData_ChangesToTheModelAfterTheCall_AreNotSaved()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("{}"u8.ToArray(), blobVersionId: BlobVersion(1));
        DataElement formDataElement = AddPersistedPaymentForm(
            setup,
            new PaymentForm { Status = "created" },
            BlobVersion(1)
        );
        var replacement = new PaymentForm { Status = "replaced" };

        await setup.DataMutator.ReplaceFormData(formDataElement, replacement);
        replacement.Status = "changed after the call";
        await setup.DataMutator.SaveChanges(setup.DataMutator.GetDataElementChanges(initializeAltinnRowId: false));

        Assert.Equal(
            SerializePaymentForm(setup, formDataElement, new PaymentForm { Status = "replaced" }),
            GetStoredData(setup, formDataElement)
        );
    }

    [Fact]
    public async Task ReplaceFormData_KeepsTheObjectGetFormDataReturned_AndReturnsIt()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("{}"u8.ToArray(), blobVersionId: BlobVersion(1));
        DataElement formDataElement = AddPersistedPaymentForm(
            setup,
            new PaymentForm { Status = "created" },
            BlobVersion(1)
        );
        PaymentForm form = await setup.DataMutator.GetFormData<PaymentForm>(formDataElement);

        PaymentForm returned = await setup.DataMutator.ReplaceFormData(
            formDataElement,
            new PaymentForm { Status = "replaced" }
        );
        form.CustomerName = "Changed through the first object";
        await setup.DataMutator.SaveChanges(setup.DataMutator.GetDataElementChanges(initializeAltinnRowId: false));

        Assert.Same(form, returned);
        Assert.Same(form, await setup.DataMutator.GetFormData<PaymentForm>(formDataElement));
        Assert.Equal(
            SerializePaymentForm(
                setup,
                formDataElement,
                new PaymentForm { Status = "replaced", CustomerName = "Changed through the first object" }
            ),
            GetStoredData(setup, formDataElement)
        );
    }

    [Fact]
    public async Task ReplaceFormData_GivesRowsWithoutARowIdOne()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("{}"u8.ToArray(), blobVersionId: BlobVersion(1));
        DataElement formDataElement = AddPersistedPaymentForm(
            setup,
            new PaymentForm { Status = "created" },
            BlobVersion(1)
        );

        await setup.DataMutator.ReplaceFormData(
            formDataElement,
            new PaymentForm { Lines = [new PaymentLine { Description = "first" }, new PaymentLine()] }
        );
        await setup.DataMutator.SaveChanges(setup.DataMutator.GetDataElementChanges(initializeAltinnRowId: false));

        PaymentForm stored = JsonSerializer.Deserialize<PaymentForm>(
            GetStoredData(setup, formDataElement),
            JsonSerializerOptions.Web
        )!;
        Assert.All(stored.Lines!, line => Assert.NotEqual(Guid.Empty, line.AltinnRowId));
    }

    [Fact]
    public async Task ReplaceFormData_WithAModelOfAnotherClass_Throws()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("{}"u8.ToArray(), blobVersionId: BlobVersion(1));
        DataElement formDataElement = AddPersistedPaymentForm(
            setup,
            new PaymentForm { Status = "created" },
            BlobVersion(1)
        );

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            setup.DataMutator.ReplaceFormData(formDataElement, new object())
        );

        Assert.Contains("applicationmetadata.json specifies", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaceFormData_ForAnAddedElement_Throws()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("{}"u8.ToArray(), blobVersionId: BlobVersion(1));
        DataElement formDataElement = AddPersistedPaymentForm(
            setup,
            new PaymentForm { Status = "created" },
            BlobVersion(1)
        );
        FormDataChange added = setup.DataMutator.AddFormDataElement(formDataElement.DataType, new PaymentForm());

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            setup.DataMutator.ReplaceFormData(added.DataElementIdentifier, new PaymentForm())
        );

        Assert.Contains("change the object passed to AddFormDataElement", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaceFormData_ForAnAddedElementAfterTheSave_ReplacesIt()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("{}"u8.ToArray(), blobVersionId: BlobVersion(1));
        DataElement formDataElement = AddPersistedPaymentForm(
            setup,
            new PaymentForm { Status = "created" },
            BlobVersion(1)
        );
        FormDataChange added = setup.DataMutator.AddFormDataElement(formDataElement.DataType, new PaymentForm());
        await setup.DataMutator.SaveChanges(setup.DataMutator.GetDataElementChanges(initializeAltinnRowId: false));

        await setup.DataMutator.ReplaceFormData(added.DataElementIdentifier, new PaymentForm { Status = "replaced" });

        PaymentForm current = await setup.DataMutator.GetFormData<PaymentForm>(added.DataElementIdentifier);
        Assert.Equal("replaced", current.Status);
    }

    [Fact]
    public async Task ReplaceFormData_ForARemovedElement_Throws()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("{}"u8.ToArray(), blobVersionId: BlobVersion(1));
        DataElement formDataElement = AddPersistedPaymentForm(
            setup,
            new PaymentForm { Status = "created" },
            BlobVersion(1)
        );
        setup.DataMutator.RemoveDataElement(formDataElement);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            setup.DataMutator.ReplaceFormData(formDataElement, new PaymentForm())
        );

        Assert.Contains("is marked for deletion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaceFormData_ForABinaryElement_Throws()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("{}"u8.ToArray(), blobVersionId: BlobVersion(1));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            setup.DataMutator.ReplaceFormData(setup.DataElement, new PaymentForm())
        );

        Assert.Contains("does not have a class reference", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangedElementMetadata_OnAnExistingElement_SendsAMetadataOnlyUpdate()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());
        var referencedId = Guid.NewGuid();

        SetAllMetadata(setup.DataMutator.GetDataElement(setup.DataElement), referencedId);
        await SaveChanges(setup);

        StorageInstanceMutationUpdateDataElement update = Assert.Single(GetSingleMutation(setup).UpdateDataElements);
        Assert.Equal(Guid.Parse(setup.DataElement.Id), update.DataElementId);
        Assert.Null(update.ContentPartName);
        Assert.Null(update.Locked);
        AssertAllMetadata(
            update.Tags,
            update.Metadata,
            update.UserDefinedMetadata,
            update.Refs,
            update.Filename,
            referencedId
        );
    }

    [Fact]
    public async Task ChangedElementMetadata_SendsOnlyTheChangedProperties()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());

        setup.DataMutator.GetDataElement(setup.DataElement).Tags.Add("signed");
        await SaveChanges(setup);

        StorageInstanceMutationUpdateDataElement update = Assert.Single(GetSingleMutation(setup).UpdateDataElements);
        Assert.Equal(["signed"], update.Tags);
        Assert.Null(update.Metadata);
        Assert.Null(update.UserDefinedMetadata);
        Assert.Null(update.Refs);
        Assert.Null(update.Filename);
    }

    [Fact]
    public async Task ChangedElementMetadata_OnAnExistingElement_SavesWithTheDataTypesAuthenticationMethod()
    {
        AuthenticationResolutionSetup setup = CreateAuthenticationResolutionSetup(1);
        StorageAuthenticationMethod method = StorageAuthenticationMethod.ServiceOwner("scope:a");
        setup.UnitOfWork.OverrideAuthenticationMethod(setup.DataTypes[0], method);

        setup.UnitOfWork.GetDataElement(setup.DataElements[0]).Tags = ["signed"];
        await setup.UnitOfWork.SaveChanges(setup.UnitOfWork.GetDataElementChanges(initializeAltinnRowId: false));

        Assert.Same(method, setup.AuthenticationMethod);
    }

    [Fact]
    public async Task ChangedElementMetadata_WithAContentUpdate_SendsOneUpdateItem()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());
        var referencedId = Guid.NewGuid();
        setup.DataMutator.UpdateBinaryDataElement(
            setup.DataElement,
            setup.DataElement.ContentType!,
            "updated"u8.ToArray()
        );

        SetAllMetadata(setup.DataMutator.GetDataElement(setup.DataElement), referencedId);
        await SaveChanges(setup);

        StorageInstanceMutationUpdateDataElement update = Assert.Single(GetSingleMutation(setup).UpdateDataElements);
        Assert.NotNull(update.ContentPartName);
        AssertAllMetadata(
            update.Tags,
            update.Metadata,
            update.UserDefinedMetadata,
            update.Refs,
            update.Filename,
            referencedId
        );
    }

    [Fact]
    public async Task ChangedElementMetadata_WithALockStatus_SendsOneUpdateItem()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());
        setup.DataMutator.LockDataElementsForDataType(setup.DataElement.DataType);

        setup.DataMutator.GetDataElement(setup.DataElement).Tags = ["signed"];
        await SaveChanges(setup);

        StorageInstanceMutationUpdateDataElement update = Assert.Single(GetSingleMutation(setup).UpdateDataElements);
        Assert.Null(update.ContentPartName);
        Assert.True(update.Locked);
        Assert.Equal(["signed"], update.Tags);
        Assert.True(setup.DataMutator.GetDataElement(setup.DataElement).Locked);
    }

    [Fact]
    public async Task ElementMetadata_OnAnAddedElement_IsSentWithTheCreate()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());
        var referencedId = Guid.Parse(setup.DataElement.Id);
        BinaryDataChange added = setup.DataMutator.AddBinaryDataElement(
            "receipt",
            "application/json",
            "receipt.json",
            "{}"u8.ToArray(),
            metadata: [new KeyValueEntry { Key = "from-add", Value = "1" }]
        );

        SetAllMetadata(added.DataElement!, referencedId);
        await SaveChanges(setup);

        StorageInstanceMutationRequest mutation = GetSingleMutation(setup);
        Assert.Empty(mutation.UpdateDataElements);
        StorageInstanceMutationCreateDataElement create = Assert.Single(mutation.CreateDataElements);
        AssertAllMetadata(
            create.Tags,
            create.Metadata,
            create.UserDefinedMetadata,
            create.Refs,
            create.Filename,
            referencedId
        );
    }

    [Fact]
    public async Task ElementMetadata_OnAnAddedElement_KeepsTheValuesGivenToTheAdd()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());
        setup.DataMutator.AddBinaryDataElement(
            "receipt",
            "application/json",
            "receipt.json",
            "{}"u8.ToArray(),
            metadata: [new KeyValueEntry { Key = "from-add", Value = "1" }]
        );

        await SaveChanges(setup);

        StorageInstanceMutationCreateDataElement create = Assert.Single(GetSingleMutation(setup).CreateDataElements);
        Assert.Equal("receipt.json", create.Filename);
        Assert.Equal([("from-add", "1")], create.Metadata?.Select(entry => (entry.Key, entry.Value)));
        Assert.Null(create.Tags);
    }

    [Fact]
    public async Task ElementMetadata_SetToEqualValues_SendsNothing()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create(
            "initial"u8.ToArray(),
            configureInstance: instance =>
            {
                instance.Data[0].Tags = ["signed"];
                instance.Data[0].Metadata = [new KeyValueEntry { Key = "archive", Value = "yes" }];
            }
        );
        DataElement dataElement = setup.DataMutator.GetDataElement(setup.DataElement);

        dataElement.Tags = ["signed"];
        dataElement.Metadata = [new KeyValueEntry { Key = "archive", Value = "yes" }];
        dataElement.Filename = setup.DataElement.Filename;
        await SaveChanges(setup);

        Assert.Empty(GetMutationRequests(setup));
    }

    [Fact]
    public async Task ElementMetadata_WithARefToAnAddedElement_ThrowsOnSave()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());
        BinaryDataChange added = setup.DataMutator.AddBinaryDataElement(
            "receipt",
            "application/json",
            "receipt.json",
            "{}"u8.ToArray()
        );

        setup.DataMutator.GetDataElement(setup.DataElement).Refs = [added.DataElementIdentifier.Guid];

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SaveChanges(setup)
        );
        Assert.Contains("its id is assigned when the instance is saved", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ElementMetadata_WithTheFilenameRemoved_ThrowsOnSave()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());

        setup.DataMutator.GetDataElement(setup.DataElement).Filename = null;

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SaveChanges(setup)
        );
        Assert.Contains("Storage cannot remove a filename", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveDataElement_AfterChangingItsMetadata_SendsOnlyTheDelete()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());
        setup.DataMutator.GetDataElement(setup.DataElement).Tags = ["signed"];

        setup.DataMutator.RemoveDataElement(setup.DataElement);
        await SaveChanges(setup);

        StorageInstanceMutationRequest mutation = GetSingleMutation(setup);
        Assert.Empty(mutation.UpdateDataElements);
        Assert.Equal(Guid.Parse(setup.DataElement.Id), Assert.Single(mutation.DeleteDataElements).DataElementId);
    }

    [Fact]
    public async Task ChangingAnotherElementProperty_ThrowsOnSave()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());

        setup.DataMutator.GetDataElement(setup.DataElement).ContentType = "text/plain";

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SaveChanges(setup)
        );
        Assert.Contains(
            $"DataElement {setup.DataElement.Id}.ContentType was changed",
            exception.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task ChangingAnotherInstanceProperty_ThrowsOnSave()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());

        setup.DataMutator.Instance.DueBefore = DateTime.UtcNow.AddDays(1);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SaveChanges(setup)
        );
        Assert.Contains("Instance.DueBefore was changed", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ChangedDataValues_AreSent_AndARemovedOrEmptyValueRemovesTheKey(string? removingValue)
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create(
            "initial"u8.ToArray(),
            configureInstance: instance =>
                instance.DataValues = new()
                {
                    ["removed"] = "old",
                    ["emptied"] = "old",
                    ["kept"] = "same",
                }
        );
        Dictionary<string, string?> dataValues = setup.DataMutator.Instance.DataValues!;

        dataValues["added"] = "new";
        dataValues.Remove("removed");
        dataValues["emptied"] = removingValue;
        await SaveChanges(setup);

        Assert.Equal(
            new Dictionary<string, string?>
            {
                ["added"] = "new",
                ["removed"] = null,
                ["emptied"] = null,
            },
            GetSingleMutation(setup).DataValues
        );
    }

    [Fact]
    public async Task ChangedPresentationTexts_AreSent()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create(
            "initial"u8.ToArray(),
            configureInstance: instance => instance.PresentationTexts = new() { ["removed"] = "old" }
        );

        setup.DataMutator.Instance.PresentationTexts = new() { ["added"] = "new" };
        await SaveChanges(setup);

        Assert.Equal(
            new Dictionary<string, string?> { ["added"] = "new", ["removed"] = null },
            GetSingleMutation(setup).PresentationTexts
        );
    }

    [Fact]
    public async Task ChangingAKeyInDataFields_ThrowsOnSave()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());
        setup.Services.AppMetadata.DataFields = [DerivedField()];

        setup.DataMutator.Instance.DataValues = new() { ["derived"] = "x" };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SaveChanges(setup)
        );
        Assert.Contains("derived from dataFields", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingAKeyInPresentationFields_ThrowsOnSave()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());
        setup.Services.AppMetadata.PresentationFields = [DerivedField()];

        setup.DataMutator.Instance.PresentationTexts = new() { ["derived"] = "x" };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SaveChanges(setup)
        );
        Assert.Contains("derived from presentationFields", exception.Message, StringComparison.Ordinal);
    }

    private static DataField DerivedField() =>
        new()
        {
            Id = "derived",
            DataTypeId = "payment-form",
            Path = nameof(PaymentForm.Status),
        };

    [Fact]
    public async Task SaveChanges_CalledTwice_SendsChangedElementMetadataOnce()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());
        setup.DataMutator.GetDataElement(setup.DataElement).Tags = ["signed"];

        await SaveChanges(setup);
        await SaveChanges(setup);

        Assert.Single(GetMutationRequests(setup));
    }

    [Fact]
    public async Task SaveChanges_CalledTwice_SendsAChangedPresentationTextOnce()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());
        setup.DataMutator.Instance.PresentationTexts = new() { ["title"] = "Application" };

        await SaveChanges(setup);
        await SaveChanges(setup);

        Assert.Single(GetMutationRequests(setup));
    }

    [Fact]
    public async Task VerifyUnchangedSincePreviousChanges_AfterSavingInstanceChanges_DoesNotThrow()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());
        setup.DataMutator.Instance.DataValues = new() { ["status"] = "paid" };
        setup.DataMutator.GetDataElement(setup.DataElement).Tags = ["signed"];
        DataElementChanges changes = setup.DataMutator.GetDataElementChanges(initializeAltinnRowId: false);
        await setup.DataMutator.SaveChanges(changes);

        setup.DataMutator.VerifyUnchangedSincePreviousChanges(changes);
    }

    [Fact]
    public async Task VerifyUnchangedSincePreviousChanges_WhenAValidatorChangesTheInstance_Throws()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());
        DataElementChanges changes = setup.DataMutator.GetDataElementChanges(initializeAltinnRowId: false);
        await setup.DataMutator.SaveChanges(changes);

        setup.DataMutator.Instance.PresentationTexts = new() { ["title"] = "Changed in a validator" };
        setup.DataMutator.GetDataElement(setup.DataElement).Tags = ["validated"];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            setup.DataMutator.VerifyUnchangedSincePreviousChanges(changes)
        );
        Assert.Equal(
            $"The instance has been changed by validators: presentation text 'title', metadata of data element {setup.DataElement.Id}",
            exception.Message
        );
    }

    [Fact]
    public async Task VerifyUnchangedSincePreviousChanges_WhenAValidatorChangesAnotherInstanceProperty_Throws()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());
        DataElementChanges changes = setup.DataMutator.GetDataElementChanges(initializeAltinnRowId: false);
        await setup.DataMutator.SaveChanges(changes);

        setup.DataMutator.Instance.DueBefore = DateTime.UtcNow.AddDays(1);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            setup.DataMutator.VerifyUnchangedSincePreviousChanges(changes)
        );
        Assert.Equal("The instance has been changed by validators", exception.Message);
        Assert.Contains("Instance.DueBefore was changed", exception.InnerException?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDataElementChanges_HoldsTheInstanceChanges_AndTheSaveSendsOnlyThose()
    {
        await using var setup = await BinaryDataUnitOfWorkSetup.Create("initial"u8.ToArray());
        setup.DataMutator.Instance.DataValues = new() { ["collected"] = "yes" };
        setup.DataMutator.GetDataElement(setup.DataElement).Tags = ["signed"];

        DataElementChanges changes = setup.DataMutator.GetDataElementChanges(initializeAltinnRowId: false);
        setup.DataMutator.Instance.PresentationTexts = new() { ["afterwards"] = "not collected" };
        await setup.DataMutator.SaveChanges(changes);

        Assert.Empty(changes.AllChanges);
        Assert.Equal(new Dictionary<string, string?> { ["collected"] = "yes" }, changes.InstanceChanges.DataValues);
        Assert.Equal(["signed"], Assert.Single(changes.DataElementMetadataChanges).Value.Tags);
        StorageInstanceMutationRequest mutation = GetSingleMutation(setup);
        Assert.Equal(new Dictionary<string, string?> { ["collected"] = "yes" }, mutation.DataValues);
        Assert.Empty(mutation.PresentationTexts);
    }

    private static void SetAllMetadata(DataElement dataElement, Guid referencedId)
    {
        dataElement.Tags = ["signed"];
        dataElement.Metadata = [new KeyValueEntry { Key = "archive", Value = "yes" }];
        dataElement.UserDefinedMetadata = [new KeyValueEntry { Key = "case", Value = "42" }];
        dataElement.Refs = [referencedId];
        dataElement.Filename = "renamed.json";
    }

    private static void AssertAllMetadata(
        List<string>? tags,
        List<KeyValueEntry>? metadata,
        List<KeyValueEntry>? userDefinedMetadata,
        List<Guid>? refs,
        string? filename,
        Guid referencedId
    )
    {
        Assert.Equal(["signed"], tags);
        Assert.Equal([("archive", "yes")], metadata?.Select(entry => (entry.Key, entry.Value)));
        Assert.Equal([("case", "42")], userDefinedMetadata?.Select(entry => (entry.Key, entry.Value)));
        Assert.Equal([referencedId], refs);
        Assert.Equal("renamed.json", filename);
    }

    private static Task SaveChanges(BinaryDataUnitOfWorkSetup setup) =>
        setup.DataMutator.SaveChanges(setup.DataMutator.GetDataElementChanges(initializeAltinnRowId: false));

    private static RequestResponse[] GetMutationRequests(BinaryDataUnitOfWorkSetup setup) =>
        setup
            .Services.Storage.RequestsResponses.Where(request =>
                request.RequestMethod == HttpMethod.Post
                && request.RequestUrl?.AbsolutePath.EndsWith("/mutations", StringComparison.Ordinal) == true
            )
            .ToArray();

    private static StorageInstanceMutationRequest GetSingleMutation(BinaryDataUnitOfWorkSetup setup) =>
        DeserializeMutationRequest(Assert.Single(GetMutationRequests(setup)).RequestBody!);

    private static byte[] GetStoredData(BinaryDataUnitOfWorkSetup setup, DataElement dataElement) =>
        setup.Services.Storage.GetInstanceAndData(setup.InstanceOwnerPartyId, setup.InstanceGuid).data[dataElement.Id];
}
