using System.Collections.Frozen;
using Altinn.App.Core.Internal.Language;

namespace Altinn.App.Core.Internal.Texts;

/// <summary>
/// Builds the language → text dictionary for <see cref="BackendTextResource.Texts"/>.
/// </summary>
internal static class LocalizedText
{
    public static FrozenDictionary<string, string> Create(string nb, string nn, string en) =>
        new Dictionary<string, string>
        {
            [LanguageConst.Nb] = nb,
            [LanguageConst.Nn] = nn,
            [LanguageConst.En] = en,
        }.ToFrozenDictionary();

    /// <summary>
    /// A text that is the same in every language. It is stored under English, which every language falls back to.
    /// </summary>
    public static FrozenDictionary<string, string> Invariant(string text) =>
        new Dictionary<string, string> { [LanguageConst.En] = text }.ToFrozenDictionary();
}
