using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Altinn.App.Core.Internal.App;
using Altinn.App.Core.Models;
using Altinn.Platform.Storage.Interface.Models;
using CoreAppFiles = Altinn.App.Core.Internal.App.AppFiles;

namespace Altinn.App.Api.Tests.Fixtures.AppFiles;

/// <summary>
/// Builds the <see cref="CoreAppFiles"/> snapshot an app under test runs with. Every <c>With</c> method returns a
/// new builder, so a class can keep its builder and a test derive a changed one from it. Start from
/// <see cref="Minimal"/> for an app built in code or <see cref="FromDirectory"/> for one checked in under
/// <c>TestData/apps</c>, and finish with <see cref="Build"/>.
/// </summary>
public sealed class AppFilesBuilder
{
    private static readonly JsonSerializerOptions _readOptions = AppFileJson.CreateSerializerOptions(
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true }
    );
    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly byte[] _applicationMetadata;
    private readonly byte[]? _xacmlPolicy;
    private readonly byte[]? _processDefinition;
    private readonly byte[]? _frontendAssets;
    private readonly ImmutableSortedDictionary<string, byte[]> _textResources;
    private readonly ImmutableSortedDictionary<string, AppModelFiles> _models;
    private readonly ImmutableSortedDictionary<string, byte[]> _options;
    private readonly byte[]? _uiSettings;
    private readonly byte[]? _footer;
    private readonly ImmutableSortedDictionary<string, UiFolderFiles> _uiFolders;
    private readonly ImmutableArray<string> _customCssFileNames;
    private readonly ImmutableArray<string> _customJsFileNames;

    private AppFilesBuilder(
        byte[] applicationMetadata,
        byte[]? xacmlPolicy = null,
        byte[]? processDefinition = null,
        byte[]? frontendAssets = null,
        ImmutableSortedDictionary<string, byte[]>? textResources = null,
        ImmutableSortedDictionary<string, AppModelFiles>? models = null,
        ImmutableSortedDictionary<string, byte[]>? options = null,
        byte[]? uiSettings = null,
        byte[]? footer = null,
        ImmutableSortedDictionary<string, UiFolderFiles>? uiFolders = null,
        ImmutableArray<string> customCssFileNames = default,
        ImmutableArray<string> customJsFileNames = default
    )
    {
        _applicationMetadata = applicationMetadata;
        _xacmlPolicy = xacmlPolicy;
        _processDefinition = processDefinition;
        _frontendAssets = frontendAssets;
        _textResources = textResources ?? Empty<byte[]>();
        _models = models ?? Empty<AppModelFiles>();
        _options = options ?? Empty<byte[]>();
        _uiSettings = uiSettings;
        _footer = footer;
        _uiFolders = uiFolders ?? Empty<UiFolderFiles>();
        _customCssFileNames = customCssFileNames.IsDefault ? [] : customCssFileNames;
        _customJsFileNames = customJsFileNames.IsDefault ? [] : customJsFileNames;
    }

    private static ImmutableSortedDictionary<string, T> Empty<T>() =>
        ImmutableSortedDictionary.Create<string, T>(StringComparer.Ordinal);

    /// <summary>The application metadata as it will be written, parsed fresh on every call.</summary>
    public ApplicationMetadata ApplicationMetadata =>
        JsonSerializer.Deserialize<ApplicationMetadata>(_applicationMetadata, _readOptions)
        ?? throw new InvalidOperationException("The application metadata deserialized to null");

    public string Org => ApplicationMetadata.Org;

    public string App => ApplicationMetadata.AppIdentifier.App;

    /// <summary>
    /// A valid app built in code: metadata with a title, one <c>default</c> data type for <typeparamref name="TModel"/>
    /// in a single data task <c>Task_1</c>, a policy that lets the default test users and the service owner do
    /// everything, and the texts a form needs. Change any of it with the <c>With</c> methods.
    /// </summary>
    public static AppFilesBuilder Minimal<TModel>(string org, string app)
        where TModel : class, new() =>
        Minimal(org, app).WithDataType<TModel>("default", dataType => dataType.TaskIds = ["Task_1"]);

    /// <summary>
    /// A valid app built in code without data types: metadata with a title, a single data task <c>Task_1</c>, a
    /// policy that lets the default test users and the service owner do everything, and the texts a form needs.
    /// </summary>
    public static AppFilesBuilder Minimal(string org, string app)
    {
        var metadata = new ApplicationMetadata($"{org}/{app}")
        {
            Org = org,
            Title = new Dictionary<string, string> { ["nb"] = app, ["en"] = app },
            DataTypes = [],
            PartyTypesAllowed = new PartyTypesAllowed
            {
                BankruptcyEstate = true,
                Organisation = true,
                Person = true,
                SubUnit = true,
            },
        };
        return new AppFilesBuilder(Serialize(metadata))
            .WithProcess(BpmnBuilder.LinearProcess(("Task_1", "data")))
            .WithPolicy(XacmlPolicyBuilder.Permissive(org, app))
            .WithTexts("nb", new Dictionary<string, string> { ["appName"] = app, ["appOwner"] = org });
    }

    /// <summary>
    /// The app in <paramref name="path"/>, loaded the way the host loads an app. Use <see cref="TestApps"/> for the
    /// apps checked in with this project.
    /// </summary>
    public static AppFilesBuilder FromDirectory(string path)
    {
        CoreAppFiles files = AppFilesLoader.Load(path);
        ApplicationMetadata metadata = ApplicationMetadataParser.Parse(files);

        var models = Empty<AppModelFiles>().ToBuilder();
        foreach (DataType dataType in metadata.DataTypes ?? [])
        {
            if (files.GetModelFiles(dataType.Id) is { } modelFiles)
            {
                models[dataType.Id] = modelFiles;
            }
        }

        var folders = Empty<UiFolderFiles>().ToBuilder();
        foreach (string folderId in files.Ui.GetFolderIds())
        {
            folders[folderId] = files.Ui.GetFolder(folderId)!;
        }

        return new AppFilesBuilder(
            files.ApplicationMetadata.ToArray(),
            TryGet(() => files.XacmlPolicy),
            TryGet(() => files.ProcessDefinition),
            files.FrontendAssets?.ToArray(),
            files
                .GetTextResourceLanguages()
                .ToImmutableSortedDictionary(
                    language => language,
                    language => files.GetTextResource(language)!.Value.ToArray(),
                    StringComparer.Ordinal
                ),
            models.ToImmutable(),
            files
                .GetOptionIds()
                .ToImmutableSortedDictionary(
                    id => id,
                    id => files.GetOptions(id)!.Value.ToArray(),
                    StringComparer.Ordinal
                ),
            files.Ui.Settings?.ToArray(),
            files.Ui.Footer?.ToArray(),
            folders.ToImmutable(),
            [.. files.GetCustomCssFileNames()],
            [.. files.GetCustomJsFileNames()]
        );

        static byte[]? TryGet(Func<ReadOnlyMemory<byte>> read)
        {
            try
            {
                return read().ToArray();
            }
            catch (Exception e) when (e is FileNotFoundException or ApplicationConfigException)
            {
                return null;
            }
        }
    }

    /// <summary>Changes <c>config/applicationmetadata.json</c>.</summary>
    public AppFilesBuilder WithApplicationMetadata(Action<ApplicationMetadata> configure)
    {
        ApplicationMetadata metadata = ApplicationMetadata;
        configure(metadata);
        return With(applicationMetadata: Serialize(metadata));
    }

    /// <summary>
    /// Adds or replaces a data type whose data is <typeparamref name="TModel"/>, stored as XML, with the class
    /// reference the app resolves the model by. The model files are added when given.
    /// </summary>
    public AppFilesBuilder WithDataType<TModel>(
        string id,
        Action<DataType>? configure = null,
        string? jsonSchema = null,
        string? xsdSchema = null,
        string? prefill = null,
        string? validationConfiguration = null,
        string? calculationConfiguration = null
    )
        where TModel : class, new()
    {
        AppFilesBuilder builder = WithDataType(
            id,
            dataType =>
            {
                dataType.AllowedContentTypes ??= ["application/xml"];
                dataType.MaxCount = dataType.MaxCount == 0 ? 1 : dataType.MaxCount;
                dataType.AppLogic ??= new ApplicationLogic();
                dataType.AppLogic.ClassRef = typeof(TModel).FullName;
                configure?.Invoke(dataType);
            }
        );
        if (
            jsonSchema is null
            && xsdSchema is null
            && prefill is null
            && validationConfiguration is null
            && calculationConfiguration is null
        )
        {
            return builder;
        }

        AppModelFiles? existing = _models.GetValueOrDefault(id);
        var modelFiles = new AppModelFiles(
            Bytes(jsonSchema) ?? existing?.JsonSchema,
            Bytes(xsdSchema) ?? existing?.XsdSchema,
            Bytes(prefill) ?? existing?.Prefill,
            Bytes(validationConfiguration) ?? existing?.ValidationConfiguration,
            Bytes(calculationConfiguration) ?? existing?.CalculationConfiguration
        );
        return builder.With(models: builder._models.SetItem(id, modelFiles));
    }

    /// <summary>Adds or replaces a data type without a model, such as an attachment type.</summary>
    public AppFilesBuilder WithDataType(string id, Action<DataType>? configure = null) =>
        WithApplicationMetadata(metadata =>
        {
            metadata.DataTypes ??= [];
            DataType? dataType = metadata.DataTypes.Find(d => d.Id == id);
            if (dataType is null)
            {
                dataType = new DataType { Id = id };
                metadata.DataTypes.Add(dataType);
            }
            configure?.Invoke(dataType);
        });

    /// <summary>Replaces <c>config/process/process.bpmn</c>.</summary>
    public AppFilesBuilder WithProcess(string bpmn) => With(processDefinition: Bytes(bpmn));

    public AppFilesBuilder WithProcess(BpmnBuilder process) => WithProcess(process.Build());

    /// <summary>Replaces <c>config/authorization/policy.xml</c>.</summary>
    public AppFilesBuilder WithPolicy(string xacml) => With(xacmlPolicy: Bytes(xacml));

    public AppFilesBuilder WithPolicy(XacmlPolicyBuilder policy) => WithPolicy(policy.Build());

    /// <summary>Replaces <c>config/texts/resource.{language}.json</c> with the given texts.</summary>
    public AppFilesBuilder WithTexts(string language, IReadOnlyDictionary<string, string> texts)
    {
        var resources = new
        {
            language,
            resources = texts.Select(text => new { id = text.Key, value = text.Value }).ToArray(),
        };
        return With(
            textResources: _textResources.SetItem(
                language,
                JsonSerializer.SerializeToUtf8Bytes(resources, _writeOptions)
            )
        );
    }

    /// <summary>Adds or replaces <c>options/{id}.json</c>.</summary>
    public AppFilesBuilder WithOptions(string id, string json) => With(options: _options.SetItem(id, Bytes(json)!));

    /// <summary>
    /// Adds or replaces the layout folder <c>ui/{id}/</c>. Folders are named after the task they belong to.
    /// </summary>
    public AppFilesBuilder WithLayoutSet(string id, Action<LayoutSetBuilder> configure)
    {
        var layoutSet = new LayoutSetBuilder(_uiFolders.GetValueOrDefault(id));
        configure(layoutSet);
        return With(uiFolders: _uiFolders.SetItem(id, layoutSet.Build()));
    }

    /// <summary>Adds or replaces one page, <c>ui/{folder}/layouts/{page}.json</c>.</summary>
    public AppFilesBuilder WithLayout(string folder, string page, string json) =>
        WithLayoutSet(folder, layoutSet => layoutSet.Layout(page, json));

    /// <summary>Replaces <c>ui/Settings.json</c>.</summary>
    public AppFilesBuilder WithUiSettings(string json) => With(uiSettings: Bytes(json));

    /// <summary>Replaces <c>ui/footer.json</c>.</summary>
    public AppFilesBuilder WithFooter(string json) => With(footer: Bytes(json));

    /// <summary>Replaces <c>config/assets.json</c>.</summary>
    public AppFilesBuilder WithFrontendAssets(string json) => With(frontendAssets: Bytes(json));

    /// <summary>The file names in <c>wwwroot/custom-css/</c>.</summary>
    public AppFilesBuilder WithCustomCssFileNames(params string[] fileNames) =>
        With(customCssFileNames: [.. fileNames.Order(StringComparer.Ordinal)]);

    /// <summary>The file names in <c>wwwroot/custom-js/</c>.</summary>
    public AppFilesBuilder WithCustomJsFileNames(params string[] fileNames) =>
        With(customJsFileNames: [.. fileNames.Order(StringComparer.Ordinal)]);

    /// <summary>The snapshot. Each call returns a new instance, which the app's caches key on.</summary>
    internal CoreAppFiles Build() =>
        new(
            _applicationMetadata,
            _xacmlPolicy,
            _processDefinition,
            _frontendAssets,
            _textResources.ToImmutableSortedDictionary(
                t => t.Key,
                t => (ReadOnlyMemory<byte>)t.Value,
                StringComparer.Ordinal
            ),
            _models,
            _options.ToImmutableSortedDictionary(
                o => o.Key,
                o => (ReadOnlyMemory<byte>)o.Value,
                StringComparer.Ordinal
            ),
            new UiFiles(_uiSettings, _footer, _uiFolders),
            _customCssFileNames,
            _customJsFileNames
        );

    private AppFilesBuilder With(
        byte[]? applicationMetadata = null,
        byte[]? xacmlPolicy = null,
        byte[]? processDefinition = null,
        byte[]? frontendAssets = null,
        ImmutableSortedDictionary<string, byte[]>? textResources = null,
        ImmutableSortedDictionary<string, AppModelFiles>? models = null,
        ImmutableSortedDictionary<string, byte[]>? options = null,
        byte[]? uiSettings = null,
        byte[]? footer = null,
        ImmutableSortedDictionary<string, UiFolderFiles>? uiFolders = null,
        ImmutableArray<string>? customCssFileNames = null,
        ImmutableArray<string>? customJsFileNames = null
    ) =>
        new(
            applicationMetadata ?? _applicationMetadata,
            xacmlPolicy ?? _xacmlPolicy,
            processDefinition ?? _processDefinition,
            frontendAssets ?? _frontendAssets,
            textResources ?? _textResources,
            models ?? _models,
            options ?? _options,
            uiSettings ?? _uiSettings,
            footer ?? _footer,
            uiFolders ?? _uiFolders,
            customCssFileNames ?? _customCssFileNames,
            customJsFileNames ?? _customJsFileNames
        );

    private static byte[] Serialize(ApplicationMetadata metadata) =>
        JsonSerializer.SerializeToUtf8Bytes(metadata, _writeOptions);

    private static byte[]? Bytes(string? text) => text is null ? null : Encoding.UTF8.GetBytes(text);
}

/// <summary>
/// The files of one layout folder, <c>ui/{folder}/</c>: its <c>Settings.json</c> and its pages.
/// </summary>
public sealed class LayoutSetBuilder
{
    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private byte[]? _settings;
    private readonly SortedDictionary<string, byte[]> _layouts = new(StringComparer.Ordinal);

    internal LayoutSetBuilder(UiFolderFiles? existing)
    {
        if (existing is null)
        {
            return;
        }
        _settings = existing.Settings?.ToArray();
        foreach (string page in existing.GetLayoutPages())
        {
            _layouts[page] = existing.GetLayout(page)!.Value.ToArray();
        }
    }

    /// <summary><c>Settings.json</c> as given.</summary>
    public LayoutSetBuilder Settings(string json)
    {
        _settings = Encoding.UTF8.GetBytes(json);
        return this;
    }

    /// <summary><c>Settings.json</c> with the page order and, when given, the default data type.</summary>
    public LayoutSetBuilder Pages(IReadOnlyList<string> order, string? defaultDataType = null)
    {
        var settings = new { pages = new { order }, defaultDataType };
        _settings = JsonSerializer.SerializeToUtf8Bytes(settings, _writeOptions);
        return this;
    }

    /// <summary><c>layouts/{page}.json</c> as given.</summary>
    public LayoutSetBuilder Layout(string page, string json)
    {
        _layouts[page] = Encoding.UTF8.GetBytes(json);
        return this;
    }

    /// <summary>
    /// <c>layouts/{page}.json</c> with the given components in a <c>data.layout</c> array; each component is
    /// serialized as it is, so anonymous objects work.
    /// </summary>
    public LayoutSetBuilder Layout(string page, params object[] components)
    {
        var layout = new { data = new { layout = components } };
        _layouts[page] = JsonSerializer.SerializeToUtf8Bytes(layout, _writeOptions);
        return this;
    }

    internal UiFolderFiles Build() =>
        new(
            _settings,
            _layouts.ToImmutableSortedDictionary(l => l.Key, l => (ReadOnlyMemory<byte>)l.Value, StringComparer.Ordinal)
        );
}
