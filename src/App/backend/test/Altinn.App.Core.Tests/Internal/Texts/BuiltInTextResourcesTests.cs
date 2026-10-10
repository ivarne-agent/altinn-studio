using System.Text;
using System.Text.RegularExpressions;
using Altinn.App.Core.Features.Validation.Default;
using Altinn.App.Core.Internal.Language;
using Altinn.App.Core.Internal.Texts;

namespace Altinn.App.Core.Tests.Internal.Texts;

public class BuiltInTextResourcesTests
{
    private static readonly Regex _placeholderRegex = new(@"\{([^{}]*)\}");

    [Fact]
    public void All_HasTheValidationIssueTextsAndTheOthers()
    {
        Assert.Equal(
            BuiltInValidationIssues
                .All.Select(definition => definition.TextResource)
                .Concat(BuiltInTextResources.Other),
            BuiltInTextResources.All
        );
    }

    [Fact]
    public void KeysAreUnique()
    {
        var keys = BuiltInTextResources.All.Select(text => text.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal(keys.Count, BuiltInTextResources.ByKey.Count);
    }

    [Fact]
    public void EveryTextHasEnglish()
    {
        // English is the language every other language falls back to.
        Assert.All(BuiltInTextResources.All, text => Assert.Contains(LanguageConst.En, text.Texts.Keys));
    }

    [Fact]
    public void EveryPlaceholderIsADeclaredVariable()
    {
        Assert.All(
            BuiltInTextResources.All,
            text =>
            {
                var names = text.Variables.Select(variable => variable.Name).ToList();
                Assert.Equal(names.Count, names.Distinct().Count());
                Assert.All(
                    text.Texts.Values,
                    value =>
                        Assert.All(
                            _placeholderRegex.Matches(value),
                            match => Assert.Contains(match.Groups[1].Value, names)
                        )
                );
            }
        );
    }

    [Fact]
    public void EveryLanguageUsesTheSamePlaceholders()
    {
        Assert.All(
            BuiltInTextResources.All,
            text =>
                Assert.Single(
                    text.Texts.Values.Select(value =>
                            string.Join(",", _placeholderRegex.Matches(value).Select(m => m.Value).Order())
                        )
                        .Distinct()
                )
        );
    }

    [Fact]
    public void NoTextUsesVariablesThatNeedLayoutState()
    {
        // Every caller of these texts translates with only a language, where data model, instance context and
        // application settings variables cannot be resolved.
        Assert.All(
            BuiltInTextResources.All,
            text => Assert.DoesNotContain(text.Variables, variable => variable.NeedsLayoutState)
        );
    }

    [Fact]
    public Task Documentation()
    {
        var markdown = new StringBuilder();
        markdown.Append("# Built-in texts\n\n");
        markdown.Append("An app changes one of these texts by adding a text resource with the same key.\n");

        foreach (var text in BuiltInTextResources.All)
        {
            markdown.Append($"\n## `{text.Key}`\n\n");
            markdown.Append("| Language | Default text |\n|---|---|\n");
            if (text.Texts.Count == 1 && text.Texts.ContainsKey(LanguageConst.En))
            {
                markdown.Append($"| all | {text.Texts[LanguageConst.En]} |\n");
            }
            else
            {
                foreach (var language in new[] { LanguageConst.Nb, LanguageConst.Nn, LanguageConst.En })
                {
                    markdown.Append($"| {language} | {text.Texts[language]} |\n");
                }
            }

            if (text.Variables.Count > 0)
            {
                markdown.Append("\n| Variable | Source | Description |\n|---|---|---|\n");
                foreach (var variable in text.Variables)
                {
                    markdown.Append($"| `{variable.Name}` | {Source(variable)} | {variable.Description} |\n");
                }
            }
        }

        return Verify(markdown.ToString(), extension: "md");
    }

    private static string Source(BackendTextVariable variable) =>
        variable switch
        {
            CustomTextParameter => "`customTextParameters`",
            { DefaultValue: { Length: > 0 } fallback } =>
                $"`{variable.DataSource}`: `{variable.Key}`, or \"{fallback}\" when it has no value",
            _ => $"`{variable.DataSource}`: `{variable.Key}`",
        };
}
