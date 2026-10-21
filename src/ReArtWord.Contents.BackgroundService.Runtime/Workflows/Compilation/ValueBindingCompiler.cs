using System.Collections.Frozen;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Binding;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

/// <summary>外部処理を実行せず、純粋値の接続だけをSourceの設定版へ固定します。</summary>
internal static class ValueBindingCompiler
{
    private static readonly HashSet<string> routes = new(["success", "*"], StringComparer.Ordinal);
    private static string ResultPort => "result";

    /// <summary>通常の入力合成を共有し、純粋値だけで成立する接続を一度評価します。</summary>
    /// <returns>Source入力を持つ計画、または値構築の具体的な問題です。</returns>
    internal static CompilationResult Bind(CompiledWorkflow plan, NodeRegistry registry)
    {
        string configurationPeer = "configuration";
        Dictionary<string, ExecutionFrame> values = new(StringComparer.Ordinal);
        Dictionary<string, IReadOnlyDictionary<string, NodeValue>> inputs = new(StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<WireDefinition>> runtimeWires = new(StringComparer.Ordinal);
        List<NodeDefinition> resolved = [];
        List<ValidationIssue> issues = [];
        HashSet<string> publicValues = [];
        Contracts.Values.WorkflowTypeInference types = new(plan.Definition, registry.Descriptors);
        if (SubworkflowInterface.IsSubworkflow(plan.Definition))
        {
            foreach (NodeDefinition node in plan.OrderedNodes)
            {
                if (string.Equals(node.Type, SubworkflowInterface.InputType, StringComparison.Ordinal) || plan.Incoming[node.Id].Any(wire => publicValues.Contains(wire.FromNode)))
                {
                    publicValues.Add(node.Id);
                }
            }
        }
        foreach (NodeDefinition node in plan.OrderedNodes)
        {
            INode implementation = registry.Find(node.Type)!;
            WireDefinition[] deferred = plan.Incoming[node.Id].Where(wire => (SettingPorts.AllowsRuntimeValue(implementation.Descriptor, wire.ToPort)
                    || SettingsBinding.IsPort(wire.ToPort) && publicValues.Contains(wire.FromNode))
                && !CanBind(wire, values)).ToArray();
            WireDefinition[] settings = plan.Incoming[node.Id].Where(wire => SettingsBinding.IsPort(wire.ToPort) && !deferred.Contains(wire)).ToArray();
            if (!settings.All(wire => CanBind(wire, values) && values[wire.FromNode].Outcome == EOutcome.Succeeded))
            {
                issues.Add(new("settings.binding.static", "Settings can only be connected to successfully supplied pure configuration values.", node.Id));
                continue;
            }
            IReadOnlyDictionary<string, NodeValue> settingValues = BindInputs(node, implementation.Descriptor, configurationPeer, settings, values, true, issues);
            NodeDefinition executable = SignalMergePorts.Configure(SettingsBinding.Overlay(node, implementation.Descriptor, settings, settingValues, issues), plan.Definition);
            executable = ButtonValuePorts.Configure(executable, plan.Definition);
            executable = IterationResultPorts.Configure(executable, plan.Definition);
            WireDefinition[] wires = plan.Incoming[node.Id].Where(wire => (!SettingsBinding.IsPort(wire.ToPort) || deferred.Contains(wire))
                && !string.Equals(wire.ToPort, PcSelectionPorts.Execution, StringComparison.Ordinal)).ToArray();
            runtimeWires[node.Id] = Array.AsReadOnly(wires);
            resolved.Add(executable);
            bool pureInputs = wires.All(wire => CanBind(wire, values));
            bool pcMerge = PcSelectionPorts.IsMerge(node, plan.Definition, types);
            IReadOnlyDictionary<string, NodeValue> bound = executable.Inputs ?? new Dictionary<string, NodeValue>(StringComparer.Ordinal);
            if (pureInputs)
            {
                bound = BindInputs(executable, implementation.Descriptor, configurationPeer, wires, values, false, issues);
                if (implementation is ISourceNode && wires.Any(wire => !bound.ContainsKey(wire.ToPort)))
                {
                    issues.Add(new("value.binding.missing", "A connected source configuration input was not supplied.", node.Id));
                }
            }
            NodeDefinition validation = executable;
            if (pureInputs && implementation is IValueNode or ISourceNode)
            {
                validation = executable with { Inputs = bound };
            }
            if (issues.All(issue => !string.Equals(issue.NodeId, node.Id, StringComparison.Ordinal)))
            {
                issues.AddRange(RuntimeSettingsBinding.Validate(validation, implementation, deferred));
            }
            if (!pureInputs || issues.Any(issue => string.Equals(issue.NodeId, node.Id, StringComparison.Ordinal)))
            {
                continue;
            }
            bool failed = wires.Any(wire => values[wire.FromNode].Outcome != EOutcome.Succeeded);
            if (pcMerge)
            {
                try
                {
                    NodeResult result = NodeResult.Success(PcSelectionValue.First(bound.Where(pair => 0 < ConcatPorts.Index(pair.Key)).OrderBy(pair => ConcatPorts.Index(pair.Key)).Select(pair => pair.Value)));
                    values[node.Id] = new(node.Id, result.Outcome, [new(configurationPeer, result)]);
                }
                catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or ArgumentException)
                {
                    issues.Add(new("selection.mergeInvalid", "PC候補の合流条件が不正または上限超過です。", node.Id));
                }
                continue;
            }
            if (implementation is IValueNode value)
            {
                NodeResult result = failed ? NodeResult.Failure("value.dependency", "A connected configuration value could not be constructed.")
                                           : value.Evaluate(executable, bound);
                values[node.Id] = new(node.Id, result.Outcome, [new(configurationPeer, result)]);
                continue;
            }
            if (implementation is not ISourceNode || wires.Length == 0)
            {
                continue;
            }
            if (failed)
            {
                issues.Add(new("value.binding", "A connected configuration value could not be constructed. Correct its value or type before starting this source.", node.Id));
            }
            else
            {
                inputs[node.Id] = bound.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.Copy(), StringComparer.Ordinal);
            }
        }
        if (issues.Count != 0)
        {
            return new(null, issues);
        }
        IReadOnlyDictionary<string, NodeResult> memo = values.ToFrozenDictionary(pair => pair.Key, pair =>
        {
            NodeResult result = pair.Value.Targets[0].Result;
            return result with { Outputs = result.Outputs.ToFrozenDictionary(output => output.Key, output => output.Value.Copy(), StringComparer.Ordinal) };
        }, StringComparer.Ordinal);
        CompiledWorkflow boundPlan = plan with { OrderedNodes = resolved.AsReadOnly(), Incoming = runtimeWires.ToFrozenDictionary(StringComparer.Ordinal), ConfigurationInputs = inputs.ToFrozenDictionary(StringComparer.Ordinal), ConfigurationValues = memo };
        return PcSelectionBinding.Bind(plan, boundPlan, registry, publicValues);
    }

    /// <summary>通常実行結果や失敗経路を設定の静的供給元と誤認しません。</summary>
    /// <returns>純粋評価済みの正常値経路かです。</returns>
    private static bool CanBind(WireDefinition wire, Dictionary<string, ExecutionFrame> values) => values.ContainsKey(wire.FromNode) && routes.Contains(wire.When) && !string.Equals(wire.FromPort, ResultPort, StringComparison.Ordinal) && !CompletionPorts.IsPort(wire.FromPort);

    /// <summary>共通の抽出・入力合成の失敗を設定検証結果として返します。</summary>
    /// <returns>供給できた入力、またはエラー付きの空集合です。</returns>
    private static IReadOnlyDictionary<string, NodeValue> BindInputs(NodeDefinition node, NodeDescriptor descriptor, string peer, IReadOnlyList<WireDefinition> wires, Dictionary<string, ExecutionFrame> values, bool strictPaths, List<ValidationIssue> issues)
    {
        try
        {
            return WireInputs.Create(node, peer, wires, values, null, strictPaths, descriptor);
        }
        catch (Exception exception) when (exception is FormatException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            issues.Add(new("value.binding.invalid", "A configuration input or JSON Pointer could not be evaluated.", node.Id));
            return new Dictionary<string, NodeValue>(StringComparer.Ordinal);
        }
    }
}
