using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Altinn.Studio.Cli.Upgrade.v8Tov9.CSharpApiMigration;

/// <summary>
/// Moves a text key that a <c>ValidationIssue</c> object initializer sets as <c>Description</c> to
/// <c>CustomTextKey</c>. The v8 form looked a description up as a text key; the v9 form shows it as text, so a key
/// left there would show as the key itself.
/// </summary>
/// <remarks>
/// <para>
/// Only object initializers are handled. A <c>Description</c> moves when it is a string literal that is the id of one
/// of the app's text resources. A literal that <see cref="TextResourceKeyMigration"/> renamed moves to the new id. An
/// initializer that sets <c>CustomTextKey</c> too is left alone with a warning, since v9 only uses
/// <c>CustomTextKey</c> and the framework sets <c>Description</c> from it.
/// </para>
/// <para>
/// With a semantic model, <c>Description</c> must bind to the SDK's <c>ValidationIssue</c>. Without one, only
/// initializers of a type spelled <c>ValidationIssue</c> are handled, and moves are reported as unverified.
/// </para>
/// </remarks>
internal sealed class ValidationDescriptionTextKeyMigration
{
    private const string TypeName = "ValidationIssue";
    private const string DescriptionName = "Description";
    private const string CustomTextKeyName = "CustomTextKey";

    private const string MovedSummary =
        "The v9 form shows a validation issue's Description as text instead of looking it up as a text key. "
        + "Descriptions set to one of the app's text keys now set CustomTextKey:";

    private const string BothSetSummary =
        "These validation issues set both Description and CustomTextKey. v9 only uses CustomTextKey for the message, "
        + "and the framework sets Description from it. Remove Description:";

    private readonly CSharpSourceScanner _scanner;
    private readonly IReadOnlySet<string> _textIds;
    private readonly IReadOnlyDictionary<string, string> _renamedTextIds;

    public ValidationDescriptionTextKeyMigration(
        CSharpSourceScanner scanner,
        IReadOnlySet<string> textIds,
        IReadOnlyDictionary<string, string> renamedTextIds
    )
    {
        _scanner = scanner;
        _textIds = textIds;
        _renamedTextIds = renamedTextIds;
    }

    public MigrationResult Migrate()
    {
        var moved = new List<string>();
        var bothSet = new List<string>();

        // Snapshot: Update replaces list entries, which would invalidate a live enumerator.
        foreach (var file in _scanner.Files.ToArray())
        {
            var model = file.SemanticModel;
            var moves = new Dictionary<AssignmentExpressionSyntax, string?>();
            foreach (var creation in file.Root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
            {
                if (
                    creation.Initializer is not { } initializer
                    || !initializer.IsKind(SyntaxKind.ObjectInitializerExpression)
                    || MemberAssignment(initializer, DescriptionName) is not { } description
                    || !IsValidationIssueDescription(creation, (IdentifierNameSyntax)description.Left, model)
                )
                {
                    continue;
                }

                var location = $"{file.RelativePath}:{file.GetLine(description)}";
                if (MemberAssignment(initializer, CustomTextKeyName) is not null)
                {
                    bothSet.Add(location);
                    continue;
                }

                if (
                    description.Right is not LiteralExpressionSyntax literal
                    || !literal.IsKind(SyntaxKind.StringLiteralExpression)
                    || TextKeyFor(literal.Token.ValueText) is not { } textKey
                )
                {
                    continue;
                }

                var value = literal.Token.ValueText;
                moves[description] = textKey == value ? null : textKey;
                var unverified = model is null ? " (unverified: the app could not be compiled)" : "";
                moved.Add(
                    textKey == value
                        ? $"{location}: Description = \"{value}\" -> CustomTextKey{unverified}"
                        : $"{location}: Description = \"{value}\" -> CustomTextKey = \"{textKey}\"{unverified}"
                );
            }

            if (moves.Count == 0)
            {
                continue;
            }

            var updated = file.Root.ReplaceNodes(moves.Keys, (original, rewritten) => Move(rewritten, moves[original]));
            _scanner.Update(file, updated);
        }

        var messages = new List<UpgradeMessage>();
        if (moved.Count > 0)
        {
            messages.Warn(MovedSummary);
            messages.WarnRange(moved);
        }

        if (bothSet.Count > 0)
        {
            messages.Warn(BothSetSummary);
            messages.WarnRange(bothSet);
        }

        return new MigrationResult(messages);
    }

    /// <summary>
    /// The initializer's simple assignment to the member with the given name, if any.
    /// </summary>
    private static AssignmentExpressionSyntax? MemberAssignment(InitializerExpressionSyntax initializer, string name) =>
        initializer
            .Expressions.OfType<AssignmentExpressionSyntax>()
            .FirstOrDefault(assignment =>
                assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                && assignment.Left is IdentifierNameSyntax identifier
                && identifier.Identifier.Text == name
            );

    private static bool IsValidationIssueDescription(
        BaseObjectCreationExpressionSyntax creation,
        IdentifierNameSyntax description,
        SemanticModel? model
    )
    {
        if (model is not null)
        {
            var info = model.GetSymbolInfo(description);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            return symbol is IPropertySymbol { Name: DescriptionName, ContainingType.Name: TypeName }
                && CSharpSemanticQueries.IsAltinnAppSymbol(symbol);
        }

        // Without a semantic model, only an explicit `new ValidationIssue` is certain enough.
        return creation is ObjectCreationExpressionSyntax { Type: var type }
            && type switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.Text == TypeName,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.Text == TypeName,
                AliasQualifiedNameSyntax aliased => aliased.Name.Identifier.Text == TypeName,
                _ => false,
            };
    }

    /// <summary>
    /// The text key to use for a description value, or null when the value is not one of the app's text keys.
    /// </summary>
    private string? TextKeyFor(string value)
    {
        if (_textIds.Contains(value))
            return value;

        return _renamedTextIds.TryGetValue(value, out var renamed) && _textIds.Contains(renamed) ? renamed : null;
    }

    /// <summary>
    /// Assigns the value to <c>CustomTextKey</c> instead, replacing it with <paramref name="renamedKey"/> when set.
    /// </summary>
    private static AssignmentExpressionSyntax Move(AssignmentExpressionSyntax assignment, string? renamedKey)
    {
        var left = SyntaxFactory.IdentifierName(CustomTextKeyName).WithTriviaFrom(assignment.Left);
        var right = renamedKey is null
            ? assignment.Right
            : SyntaxFactory
                .LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(renamedKey))
                .WithTriviaFrom(assignment.Right);
        return assignment.WithLeft(left).WithRight(right);
    }
}
