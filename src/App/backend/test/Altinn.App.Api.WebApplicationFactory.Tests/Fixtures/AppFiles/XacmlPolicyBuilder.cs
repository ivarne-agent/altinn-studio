using System.Security;
using System.Text;

namespace Altinn.App.Api.Tests.Fixtures.AppFiles;

/// <summary>
/// Writes a <c>policy.xml</c> of permit rules for one app, the way the apps' own policies are written: a rule
/// matches any of its subjects, the app (and task or end event when given) and any of its actions.
/// </summary>
public sealed class XacmlPolicyBuilder
{
    private readonly string _org;
    private readonly string _app;
    private readonly List<string> _rules = [];

    public XacmlPolicyBuilder(string org, string app)
    {
        _org = org;
        _app = app;
    }

    /// <summary>
    /// Lets the roles the default test users hold, and the service owner, do everything in every task. User 1337 has
    /// <c>REGNA</c> at its own party and at the organisations it represents.
    /// </summary>
    public static XacmlPolicyBuilder Permissive(string org, string app) =>
        new XacmlPolicyBuilder(org, app).Permit(
            new XacmlRule
            {
                RoleCodes = ["REGNA", "DAGL", "PRIV"],
                SubjectOrgs = [org],
                Actions = ["instantiate", "read", "write", "delete", "complete", "reject", "sign", "confirm"],
            }
        );

    public XacmlPolicyBuilder Permit(XacmlRule rule)
    {
        _rules.Add(Render(rule, _rules.Count + 1));
        return this;
    }

    public string Build()
    {
        var xml = new StringBuilder();
        xml.Append(
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <xacml:Policy xmlns:xacml="urn:oasis:names:tc:xacml:3.0:core:schema:wd-17" PolicyId="urn:altinn:{E(
                _org
            )}:{E(
                _app
            )}:policyid:1" Version="1.0" RuleCombiningAlgId="urn:oasis:names:tc:xacml:3.0:rule-combining-algorithm:deny-overrides">
              <xacml:Target/>

            """
        );
        foreach (string rule in _rules)
        {
            xml.Append(rule);
        }
        xml.Append("</xacml:Policy>\n");
        return xml.ToString();
    }

    private string Render(XacmlRule rule, int number)
    {
        const string subject = "urn:oasis:names:tc:xacml:1.0:subject-category:access-subject";
        const string resource = "urn:oasis:names:tc:xacml:3.0:attribute-category:resource";
        const string action = "urn:oasis:names:tc:xacml:3.0:attribute-category:action";
        const string ignoreCase = "urn:oasis:names:tc:xacml:3.0:function:string-equal-ignore-case";
        const string equal = "urn:oasis:names:tc:xacml:1.0:function:string-equal";

        var subjects = new List<string>();
        subjects.AddRange(rule.RoleCodes.Select(r => AllOf(Match(ignoreCase, r, "urn:altinn:rolecode", subject))));
        subjects.AddRange(rule.SubjectOrgs.Select(o => AllOf(Match(ignoreCase, o, "urn:altinn:org", subject))));
        subjects.AddRange(
            rule.OrgNumbers.Select(o => AllOf(Match(equal, o, "urn:altinn:organizationnumber", subject)))
        );
        subjects.AddRange(rule.UserIds.Select(u => AllOf(Match(equal, u.ToString(), "urn:altinn:userid", subject))));
        if (subjects.Count == 0)
        {
            throw new InvalidOperationException("A rule needs at least one subject");
        }

        var resourceMatches = new List<string>
        {
            Match(equal, _org, "urn:altinn:org", resource),
            Match(equal, _app, "urn:altinn:app", resource),
        };
        var resources = new List<string>();
        if (rule.Tasks.Count == 0 && rule.EndEvents.Count == 0)
        {
            resources.Add(AllOf(resourceMatches.ToArray()));
        }
        resources.AddRange(
            rule.Tasks.Select(t => AllOf([.. resourceMatches, Match(equal, t, "urn:altinn:task", resource)]))
        );
        resources.AddRange(
            rule.EndEvents.Select(e => AllOf([.. resourceMatches, Match(equal, e, "urn:altinn:end-event", resource)]))
        );

        var actions = rule
            .Actions.Select(a => AllOf(Match(equal, a, "urn:oasis:names:tc:xacml:1.0:action:action-id", action)))
            .ToList();
        if (actions.Count == 0)
        {
            throw new InvalidOperationException("A rule needs at least one action");
        }

        return $"""
              <xacml:Rule RuleId="urn:altinn:{E(_org)}:{E(_app)}:ruleid:{number}" Effect="Permit">
                <xacml:Description>{E(rule.Description ?? $"Rule {number}")}</xacml:Description>
                <xacml:Target>
                  <xacml:AnyOf>{string.Concat(subjects)}</xacml:AnyOf>
                  <xacml:AnyOf>{string.Concat(resources)}</xacml:AnyOf>
                  <xacml:AnyOf>{string.Concat(actions)}</xacml:AnyOf>
                </xacml:Target>
              </xacml:Rule>

            """;

        static string AllOf(params string[] matches) => $"<xacml:AllOf>{string.Concat(matches)}</xacml:AllOf>";

        static string Match(string matchId, string value, string attributeId, string category) =>
            $"""<xacml:Match MatchId="{matchId}"><xacml:AttributeValue DataType="http://www.w3.org/2001/XMLSchema#string">{E(value)}</xacml:AttributeValue><xacml:AttributeDesignator AttributeId="{attributeId}" Category="{category}" DataType="http://www.w3.org/2001/XMLSchema#string" MustBePresent="false"/></xacml:Match>""";
    }

    private static string E(string value) => SecurityElement.Escape(value);
}

/// <summary>One permit rule: any of the subjects may do any of the actions on the app, in the given tasks or end events (all when none).</summary>
public sealed record XacmlRule
{
    public string? Description { get; init; }
    public IReadOnlyList<string> RoleCodes { get; init; } = [];
    public IReadOnlyList<string> SubjectOrgs { get; init; } = [];
    public IReadOnlyList<string> OrgNumbers { get; init; } = [];
    public IReadOnlyList<int> UserIds { get; init; } = [];
    public IReadOnlyList<string> Tasks { get; init; } = [];
    public IReadOnlyList<string> EndEvents { get; init; } = [];
    public required IReadOnlyList<string> Actions { get; init; }
}
