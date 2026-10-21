using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Nodes;

/// <summary>設定スキーマと実際の入力を同じ規則で検証します。</summary>
public static class NodeSupport
{
    /// <summary>共通の有限ノード定義を作ります。</summary>
    /// <returns>設定画面へ公開する情報です。</returns>
    public static NodeDescriptor Describe(string type, string name, string category, string schema, IReadOnlyList<PortDescriptor>? inputs = null, IReadOnlyList<PortDescriptor>? outputs = null, IReadOnlyList<string>? routes = null, ENodeKind kind = ENodeKind.Action)
    {
        return new NodeDescriptor(type, name, category, name, kind, EExecutionLocation.Independent,
            inputs ?? [new PortDescriptor("value", Required: true)],
            outputs ?? [new PortDescriptor("value")], routes ?? ["success"], NodeSettings.Schema(schema));
    }

    /// <summary>型・必須項目・選択肢・下限を検証します。</summary>
    /// <returns>一致しなかった設定の一覧です。</returns>
    public static IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition, NodeDescriptor descriptor)
    {
        if (NodeBehaviorContracts.Validate(definition, descriptor) is { } incompatible)
        {
            return [incompatible];
        }
        return NodeSchemaValidator.Validate(definition, descriptor);
    }

    /// <summary>入力ポートの値を取得します。</summary>
    /// <returns>入力済み値、または未入力ならnullです。</returns>
    public static NodeValue? Input(NodeExecutionContext context, string name = "value")
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Inputs.TryGetValue(name, out NodeValue? value);
        return value;
    }

    /// <summary>実行時にも設定を検証します。</summary>
    /// <returns>不正な設定の結果、または有効ならnullです。</returns>
    public static NodeResult? Invalid(NodeExecutionContext context, INode node)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(node);
        NodeDefinition definition = new(context.NodeId, node.Descriptor.Type, context.Settings, context.Inputs, ContractVersion: context.ContractVersion);
        if (NodeBehaviorContracts.Validate(definition, node.Descriptor) is { } incompatible)
        {
            return NodeResult.Failure(incompatible.Code, incompatible.Message, EOutcome.NotExecuted);
        }
        IReadOnlyList<ValidationIssue> issues = node.Validate(definition);
        if (issues.Count == 0)
        {
            if (NetworkBindingSettings.Supports(node.Descriptor.SettingsSchema) && NetworkBindingSettings.IsPerHost(context.Settings))
            {
                try
                {
                    _ = NetworkBindingSettings.Select(context.Settings, context.HostId);
                }
                catch (ArgumentException)
                {
                    return NodeResult.Failure("network.binding.missing", "Configure exactly one network adapter and local IPv4 assignment for this execution PC.", EOutcome.NotExecuted);
                }
            }
            return null;
        }
        return NodeResult.Failure(issues[0].Code, issues[0].Message, EOutcome.NotExecuted);
    }
}
