using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>どの入力から届いても、その一件の値だけを共通の後続へ渡します。</summary>
public sealed class ValueMergeNode : IActionNode
{
    /// <summary>増設可能な値入力と、受信値・開始Signalを公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe(ValueMergePorts.NodeType, "Value merge", "Control", """{"type":"object","properties":{}}""",
        inputs: Enumerable.Range(1, ConcatPorts.Maximum).Select(index => new PortDescriptor(ConcatPorts.Name(index))).ToArray(),
        outputs: [new("value"), new("start", EPortValueKind.Json)]) with
    {
        Description = "Forward each arriving value unchanged and emit a start signal, without waiting for the other inputs. Equal values received separately remain separate events.",
    };

    /// <summary>定数を受信扱いにせず、一件を複数実行先へ重複配送しません。</summary>
    /// <returns>構成上の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor)];
        if (definition.PcExecution == EPcExecutionMode.All || definition.Target?.Scope == EExecutionScope.All
            || definition.Inputs?.Keys.Any(port => 0 < ConcatPorts.Index(port)) == true)
        {
            issues.Add(new("valueMerge.connections", "Connect value outputs and select one execution PC for a value merge.", definition.Id));
        }
        return issues;
    }

    /// <summary>受信一件に固定された入力を、変換・連結・重複排除せず返します。</summary>
    /// <returns>届いた値と一件の開始Signalです。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        string eventKey = "$signalEvent";
        NodeValue[] arrived = context.Inputs.Where(pair => 0 < ConcatPorts.Index(pair.Key)).Select(pair => pair.Value).ToArray();
        if (context.IsValueRead)
        {
            if (arrived.Length == 1)
            {
                return ValueTask.FromResult(NodeResult.Success(arrived[0].Copy()));
            }
            if (1 < arrived.Length)
            {
                return ValueTask.FromResult(NodeResult.Failure("valueMerge.ambiguous", "More than one branch supplied a value during a read. Select one branch before merging."));
            }
        }
        if (arrived.Length != 1 || !context.Inputs.TryGetValue(eventKey, out NodeValue? received)
            || received.Kind != EValueKind.Json || received.Json.ValueKind != JsonValueKind.String
            || !Guid.TryParse(received.GetString(), out Guid eventId) || eventId == Guid.Empty)
        {
            return ValueTask.FromResult(NodeResult.Failure("valueMerge.input", "One received value event is required; static reads do not start a value merge.", EOutcome.NotExecuted));
        }
        return ValueTask.FromResult(new NodeResult(EOutcome.Succeeded, "success", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["value"] = arrived[0].Copy(),
            ["start"] = NodeValue.FromObject(true),
        }));
    }
}
