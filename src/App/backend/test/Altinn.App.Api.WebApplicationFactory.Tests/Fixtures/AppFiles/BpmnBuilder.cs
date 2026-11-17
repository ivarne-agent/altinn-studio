using System.Security;
using System.Text;

namespace Altinn.App.Api.Tests.Fixtures.AppFiles;

/// <summary>
/// Writes a <c>process.bpmn</c> from a list of elements and the flows between them. <see cref="LinearProcess"/>
/// covers the common case of a start event, tasks in sequence and an end event; a gateway is a split whose outgoing
/// flows carry conditions.
/// </summary>
public sealed class BpmnBuilder
{
    private readonly string _processId;
    private readonly List<Element> _elements = [];
    private readonly List<(string Id, string Source, string Target, string? Condition)> _flows = [];

    public BpmnBuilder(string processId = "Process_1") => _processId = processId;

    /// <summary>Start event <c>StartEvent_1</c>, the given tasks in order, end event <c>EndEvent_1</c>.</summary>
    public static BpmnBuilder LinearProcess(params (string Id, string TaskType)[] tasks)
    {
        var builder = new BpmnBuilder().StartEvent("StartEvent_1");
        string previous = "StartEvent_1";
        foreach ((string id, string taskType) in tasks)
        {
            builder.Task(id, taskType).Flow(previous, id);
            previous = id;
        }
        return builder.EndEvent("EndEvent_1").Flow(previous, "EndEvent_1");
    }

    public BpmnBuilder StartEvent(string id) => Add(new Element("bpmn:startEvent", id));

    public BpmnBuilder EndEvent(string id) => Add(new Element("bpmn:endEvent", id));

    /// <summary>A task of the given Altinn task type (<c>data</c>, <c>confirmation</c>, <c>signing</c>, ...).</summary>
    public BpmnBuilder Task(string id, string taskType, string? name = null, IReadOnlyList<string>? actions = null)
    {
        var extension = new StringBuilder();
        extension.Append("<bpmn:extensionElements><altinn:taskExtension>");
        extension.Append($"<altinn:taskType>{E(taskType)}</altinn:taskType>");
        if (actions is { Count: > 0 })
        {
            extension.Append("<altinn:actions>");
            foreach (string action in actions)
            {
                extension.Append($"<altinn:action>{E(action)}</altinn:action>");
            }
            extension.Append("</altinn:actions>");
        }
        extension.Append("</altinn:taskExtension></bpmn:extensionElements>");
        return Add(new Element("bpmn:task", id, name is null ? "" : $" name=\"{E(name)}\"", extension.ToString()));
    }

    /// <summary>An exclusive gateway. Outgoing flows get their condition from <see cref="Flow"/>; one may be the default.</summary>
    public BpmnBuilder ExclusiveGateway(string id, string? defaultFlowId = null) =>
        Add(new Element("bpmn:exclusiveGateway", id, defaultFlowId is null ? "" : $" default=\"{E(defaultFlowId)}\""));

    /// <summary>
    /// A sequence flow; <paramref name="condition"/> is an expression such as <c>["equals", ["dataModel", "x"], "y"]</c>.
    /// </summary>
    public BpmnBuilder Flow(string source, string target, string? condition = null, string? id = null)
    {
        _flows.Add((id ?? $"Flow_{source}_{target}", source, target, condition));
        return this;
    }

    public string Build()
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        xml.Append(
            $"<bpmn:definitions id=\"Definitions_{E(_processId)}\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:altinn=\"http://altinn.no/process\" xmlns:bpmn=\"http://www.omg.org/spec/BPMN/20100524/MODEL\" targetNamespace=\"http://bpmn.io/schema/bpmn\">\n"
        );
        xml.Append($"  <bpmn:process id=\"{E(_processId)}\" isExecutable=\"false\">\n");
        foreach (Element element in _elements)
        {
            xml.Append($"    <{element.Tag} id=\"{E(element.Id)}\"{element.Attributes}>");
            foreach (var flow in _flows.Where(f => f.Target == element.Id))
            {
                xml.Append($"<bpmn:incoming>{E(flow.Id)}</bpmn:incoming>");
            }
            foreach (var flow in _flows.Where(f => f.Source == element.Id))
            {
                xml.Append($"<bpmn:outgoing>{E(flow.Id)}</bpmn:outgoing>");
            }
            xml.Append(element.Content);
            xml.Append($"</{element.Tag}>\n");
        }
        foreach ((string id, string source, string target, string? condition) in _flows)
        {
            xml.Append($"    <bpmn:sequenceFlow id=\"{E(id)}\" sourceRef=\"{E(source)}\" targetRef=\"{E(target)}\">");
            if (condition is not null)
            {
                xml.Append($"<bpmn:conditionExpression>{E(condition)}</bpmn:conditionExpression>");
            }
            xml.Append("</bpmn:sequenceFlow>\n");
        }
        xml.Append("  </bpmn:process>\n</bpmn:definitions>\n");
        return xml.ToString();
    }

    private BpmnBuilder Add(Element element)
    {
        _elements.Add(element);
        return this;
    }

    private static string E(string value) => SecurityElement.Escape(value);

    private sealed record Element(string Tag, string Id, string Attributes = "", string Content = "");
}
