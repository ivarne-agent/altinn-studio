namespace Altinn.App.Core.Internal.Texts;

/// <summary>
/// A variable in the default text of a <see cref="BackendTextResource"/>. Default texts refer to it by
/// <see cref="Name"/>, like <c>{maxSize}</c>, and it becomes a text resource variable with its own data source.
/// </summary>
/// <param name="Name">The placeholder name in default texts</param>
/// <param name="Description">What the value is, for documentation</param>
internal abstract record BackendTextVariable(string Name, string Description)
{
    /// <summary>The data source of the text resource variable.</summary>
    public abstract string DataSource { get; }

    /// <summary>The key of the text resource variable within its data source.</summary>
    public abstract string Key { get; }

    /// <summary>The value to use when the data source has no value.</summary>
    public virtual string? DefaultValue => null;

    /// <summary>
    /// Whether the value can only be found with layout state, which a translation in only a language does not have.
    /// </summary>
    public virtual bool NeedsLayoutState => false;
}

/// <summary>
/// A value the caller passes in customTextParameters, under <see cref="BackendTextVariable.Name"/>.
/// </summary>
internal sealed record CustomTextParameter(string Name, string Description) : BackendTextVariable(Name, Description)
{
    public override string DataSource => "customTextParameters";
    public override string Key => Name;

    // A missing parameter shows as nothing rather than as its name.
    public override string? DefaultValue => "";
}

/// <summary>
/// The translation of another text resource, such as <c>appName</c>.
/// </summary>
internal sealed record TextVariable(string Name, string TextKey, string Description, string? Fallback = null)
    : BackendTextVariable(Name, Description)
{
    public override string DataSource => "text";
    public override string Key => TextKey;
    public override string? DefaultValue => Fallback;
}

/// <summary>
/// A field in the data model of <c>DataType</c>, or of the layout set's data type for <c>default</c>.
/// </summary>
internal sealed record DataModelVariable(string Name, string DataType, string Path, string Description)
    : BackendTextVariable(Name, Description)
{
    public override string DataSource => $"dataModel.{DataType}";
    public override string Key => Path;
    public override bool NeedsLayoutState => true;
}

/// <summary>
/// A value about the instance, such as <c>instanceOwnerPartyId</c> or <c>appId</c>.
/// </summary>
internal sealed record InstanceContextVariable(string Name, string ContextKey, string Description)
    : BackendTextVariable(Name, Description)
{
    public override string DataSource => "instanceContext";
    public override string Key => ContextKey;
    public override bool NeedsLayoutState => true;
}

/// <summary>
/// A frontend setting from the app's application settings.
/// </summary>
internal sealed record ApplicationSettingVariable(string Name, string Setting, string Description)
    : BackendTextVariable(Name, Description)
{
    public override string DataSource => "applicationSettings";
    public override string Key => Setting;
    public override bool NeedsLayoutState => true;
}
