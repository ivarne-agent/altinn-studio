using System.Security.Claims;
using System.Xml;
using Altinn.App.Api.Tests.Fixtures.Platform;
using Altinn.App.Core.Constants;
using Altinn.App.Core.Internal.App;
using Altinn.App.Core.Internal.Instances;
using Altinn.Authorization.ABAC.Constants;
using Altinn.Authorization.ABAC.Utils;
using Altinn.Authorization.ABAC.Xacml;
using Altinn.Authorization.ABAC.Xacml.JsonProfile;
using Altinn.Common.PEP.Helpers;
using Altinn.Platform.Storage.Interface.Models;
using Authorization.Platform.Authorization.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Altinn.App.Api.Tests.Mocks;

/// <summary>
/// A policy decision point that evaluates the app's own <c>policy.xml</c>, read from the current app files on
/// every decision, with the roles from <see cref="TestPlatformData"/>. Enriches the request with the instance's
/// task and owner the way the platform's PDP does.
/// </summary>
public sealed class InMemoryPdp(IAppMetadata appMetadata, TestPlatformData data, IServiceProvider serviceProvider)
    : Altinn.Common.PEP.Interfaces.IPDP
{
    public Task<XacmlJsonResponse> GetDecisionForRequest(XacmlJsonRequestRoot xacmlJsonRequest) =>
        GetDecisionForRequest(xacmlJsonRequest, CancellationToken.None);

    public async Task<XacmlJsonResponse> GetDecisionForRequest(
        XacmlJsonRequestRoot xacmlJsonRequest,
        CancellationToken cancellationToken
    )
    {
        XacmlContextRequest decisionRequest = XacmlJsonXmlConverter.ConvertRequest(xacmlJsonRequest.Request);
        await EnrichResourceAttributes(decisionRequest, cancellationToken);

        XacmlPolicy policy = ParsePolicy(appMetadata.XacmlPolicy);
        XacmlContextResponse contextResponse = new Altinn.Authorization.ABAC.PolicyDecisionPoint().Authorize(
            decisionRequest,
            policy
        );
        return XacmlJsonXmlConverter.ConvertResponse(contextResponse);
    }

    public Task<bool> GetDecisionForUnvalidateRequest(XacmlJsonRequestRoot xacmlJsonRequest, ClaimsPrincipal user) =>
        GetDecisionForUnvalidateRequest(xacmlJsonRequest, user, CancellationToken.None);

    public async Task<bool> GetDecisionForUnvalidateRequest(
        XacmlJsonRequestRoot xacmlJsonRequest,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        XacmlJsonResponse response = await GetDecisionForRequest(xacmlJsonRequest, cancellationToken);
        return DecisionHelper.ValidatePdpDecision(response.Response, user);
    }

    private async Task EnrichResourceAttributes(XacmlContextRequest request, CancellationToken cancellationToken)
    {
        XacmlContextAttributes resourceContextAttributes = request.GetResourceAttributes();
        XacmlResourceAttributes resourceAttributes = GetResourceAttributeValues(resourceContextAttributes);

        bool complete =
            !string.IsNullOrEmpty(resourceAttributes.OrgValue)
            && !string.IsNullOrEmpty(resourceAttributes.AppValue)
            && !string.IsNullOrEmpty(resourceAttributes.ResourcePartyValue)
            && (
                (
                    !string.IsNullOrEmpty(resourceAttributes.InstanceValue)
                    && !string.IsNullOrEmpty(resourceAttributes.TaskValue)
                )
                || (
                    string.IsNullOrEmpty(resourceAttributes.InstanceValue)
                    && string.IsNullOrEmpty(resourceAttributes.TaskValue)
                )
                || (
                    !string.IsNullOrEmpty(resourceAttributes.InstanceValue)
                    && resourceAttributes.AppResourceValue == "events"
                )
            );

        if (!complete && !string.IsNullOrEmpty(resourceAttributes.InstanceValue))
        {
            // The instance comes from the app's own storage client, which the fake storage answers
            string[] instanceId = resourceAttributes.InstanceValue.Split('/');
            var instanceClient = serviceProvider.GetRequiredService<IInstanceClient>();
            Instance instance = await instanceClient.GetInstance(
                resourceAttributes.AppValue,
                resourceAttributes.OrgValue,
                int.Parse(instanceId[0]),
                Guid.Parse(instanceId[1]),
                authenticationMethod: null,
                cancellationToken
            );

            AddIfMissing(resourceContextAttributes, AltinnUrns.Org, resourceAttributes.OrgValue, instance.Org);
            AddIfMissing(
                resourceContextAttributes,
                AltinnUrns.App,
                resourceAttributes.AppValue,
                instance.AppId.Split('/')[1]
            );
            if (instance.Process?.CurrentTask is not null)
            {
                AddIfMissing(
                    resourceContextAttributes,
                    AltinnUrns.Task,
                    resourceAttributes.TaskValue,
                    instance.Process.CurrentTask.ElementId
                );
            }
            else if (instance.Process?.EndEvent is not null)
            {
                AddIfMissing(resourceContextAttributes, AltinnUrns.EndEvent, string.Empty, instance.Process.EndEvent);
            }
            AddIfMissing(
                resourceContextAttributes,
                AltinnUrns.PartyId,
                resourceAttributes.ResourcePartyValue,
                instance.InstanceOwner.PartyId
            );
            resourceAttributes.ResourcePartyValue = instance.InstanceOwner.PartyId;
        }

        EnrichSubjectAttributes(request, resourceAttributes.ResourcePartyValue);
    }

    private static void AddIfMissing(
        XacmlContextAttributes attributes,
        string attributeId,
        string existingValue,
        string newValue
    )
    {
        if (!string.IsNullOrEmpty(existingValue))
        {
            return;
        }
        XacmlAttribute attribute = new(new Uri(attributeId), false);
        if (attributeId == AltinnUrns.PartyId)
        {
            // When the party attribute is missing from input it is returned, so the PEP can get this information
            attribute.IncludeInResult = true;
        }
        attribute.AttributeValues.Add(new XacmlAttributeValue(new Uri(XacmlConstants.DataTypes.XMLString), newValue));
        attributes.Attributes.Add(attribute);
    }

    private void EnrichSubjectAttributes(XacmlContextRequest request, string resourceParty)
    {
        if (string.IsNullOrEmpty(resourceParty))
        {
            return;
        }

        XacmlContextAttributes subjectAttributes = request.GetSubjectAttributes();
        int userId = subjectAttributes
            .Attributes.Where(a => a.AttributeId.OriginalString == AltinnUrns.UserId)
            .Select(a => int.Parse(a.AttributeValues.First().Value))
            .FirstOrDefault();
        if (userId == 0)
        {
            return;
        }

        XacmlAttribute roleAttribute = new(new Uri(AltinnUrns.RoleCode), false);
        foreach (Role role in data.RolesFor(userId, int.Parse(resourceParty)))
        {
            roleAttribute.AttributeValues.Add(
                new XacmlAttributeValue(new Uri(XacmlConstants.DataTypes.XMLString), role.Value)
            );
        }
        subjectAttributes.Attributes.Add(roleAttribute);
    }

    private static XacmlResourceAttributes GetResourceAttributeValues(XacmlContextAttributes attributes)
    {
        var values = new XacmlResourceAttributes();
        foreach (XacmlAttribute attribute in attributes.Attributes)
        {
            string value = attribute.AttributeValues.First().Value;
            switch (attribute.AttributeId.OriginalString)
            {
                case AltinnUrns.Org:
                    values.OrgValue = value;
                    break;
                case AltinnUrns.App:
                    values.AppValue = value;
                    break;
                case AltinnUrns.InstanceId:
                    values.InstanceValue = value;
                    break;
                case AltinnUrns.PartyId:
                    values.ResourcePartyValue = value;
                    break;
                case AltinnUrns.Task:
                    values.TaskValue = value;
                    break;
                case AltinnUrns.AppResource:
                    values.AppResourceValue = value;
                    break;
            }
        }
        return values;
    }

    private static XacmlPolicy ParsePolicy(string policyXml)
    {
        using XmlReader reader = XmlReader.Create(new StringReader(policyXml));
        return XacmlParser.ParseXacmlPolicy(reader);
    }
}
